using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class DiagnosisCandidateGenerationTests
{
    [Fact]
    public void GenerateDiagnosisCandidates_MapsMeasurementJsonRules()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");
        File.WriteAllText(
            measurementsPath,
            JsonSerializer.Serialize(new[]
            {
                MeasurementJson("trace-error", "copilot-cli", "baseline", errorCount: 2),
                MeasurementJson("trace-loop", "vscode-copilot-chat", "baseline", toolCallCount: 10, successStatus: "fail"),
                MeasurementJson(null, null, null),
            }));

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);

        Assert.Equal("diagcand-0001", rows[0].GetProperty("diagnosis_candidate_id").GetString());
        Assert.Equal("DIAG-METRIC-ERROR-COUNT-V1", rows[0].GetProperty("rule_id").GetString());
        Assert.Equal("F-ERROR", rows[0].GetProperty("failure_category_id").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("anti_pattern_id").ValueKind);
        Assert.Equal("auto-eligible", rows[0].GetProperty("candidate_status").GetString());

        Assert.Equal("diagcand-0002", rows[1].GetProperty("diagnosis_candidate_id").GetString());
        Assert.Equal("DIAG-METRIC-TOOL-LOOP-V1", rows[1].GetProperty("rule_id").GetString());
        Assert.Equal("AP-TOOL-LOOP", rows[1].GetProperty("anti_pattern_id").GetString());
        Assert.Equal("candidate", rows[1].GetProperty("candidate_status").GetString());

        Assert.Equal("diagcand-0003", rows[2].GetProperty("diagnosis_candidate_id").GetString());
        Assert.Equal("DIAG-METADATA-MISSING-TRACE-CONTEXT-V1", rows[2].GetProperty("rule_id").GetString());
        Assert.Equal("F-MEASURE", rows[2].GetProperty("failure_category_id").GetString());
        Assert.Equal("eval", rows[2].GetProperty("recommended_improvement_target").GetString());
        Assert.False(File.ReadAllText(outputPath).Contains("synthetic-sensitive-value", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateDiagnosisCandidates_AcceptsMeasurementCsvAndWritesFixedHeader()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.csv");
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.csv");
        File.WriteAllLines(
            measurementsPath,
            [
                string.Join(',', MeasurementOutputWriter.Columns),
                CsvRow("trace-loop", "baseline", "copilot-cli", toolCallCount: "12", successStatus: "not-evaluated"),
            ]);

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--csv", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(string.Join(',', DiagnosisCandidateOutputWriter.Columns), lines[0]);
        Assert.Contains("diagcand-0001,trace-loop,", lines[1]);
        Assert.Contains("DIAG-METRIC-TOOL-LOOP-V1,F-TOOL,AP-TOOL-LOOP", lines[1]);
    }

    [Fact]
    public void GenerateDiagnosisCandidates_MapsRawContentRulesWithoutLeakingContent()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-raw");
        var rawPath = WriteRawOtlp(tempDirectory.Path, "trace-raw");
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--raw", rawPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var jsonText = File.ReadAllText(outputPath);
        using var document = JsonDocument.Parse(jsonText);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("DIAG-CONTENT-ERROR-MESSAGE-V1", rows[0].GetProperty("rule_id").GetString());
        Assert.Equal("raw:trace-raw:span-error:", rows[0].GetProperty("evidence_ref").GetString()![..25]);
        Assert.False(rows[0].GetProperty("content_included").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("sensitive_bundle_path").ValueKind);
        Assert.Equal("DIAG-CONTENT-SENSITIVE-LEAK-V1", rows[1].GetProperty("rule_id").GetString());
        Assert.Equal("blocking", rows[1].GetProperty("severity").GetString());
        Assert.Equal("blocked", rows[1].GetProperty("candidate_status").GetString());
        Assert.DoesNotContain("synthetic-sensitive-value", jsonText);
        Assert.DoesNotContain("synthetic prompt content should stay in bundle only", jsonText);
    }

    [Fact]
    public void GenerateDiagnosisCandidates_WritesSensitiveBundleOnlyWhenOptedIn()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-raw");
        var rawPath = WriteRawOtlp(tempDirectory.Path, "trace-raw");
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");
        var bundlePath = Path.Combine(tempDirectory.Path, "bundle");

        var exitCode = CliApplication.Run(
            [
                "generate-diagnosis-candidates",
                measurementsPath,
                "--raw",
                rawPath,
                "--include-sensitive-content",
                "--sensitive-output-dir",
                bundlePath,
                "--json",
                outputPath,
            ],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var candidatesDocument = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = candidatesDocument.RootElement.EnumerateArray().ToArray();
        Assert.All(rows, row => Assert.True(row.GetProperty("content_included").GetBoolean()));
        Assert.All(rows, row => Assert.Equal(bundlePath, row.GetProperty("sensitive_bundle_path").GetString()));
        Assert.All(rows, row => Assert.StartsWith("bundle:bundle:diagcand-", row.GetProperty("evidence_ref").GetString(), StringComparison.Ordinal));
        Assert.DoesNotContain("synthetic-sensitive-value", File.ReadAllText(outputPath));

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "manifest.json")));
        var manifest = manifestDocument.RootElement;
        Assert.Equal(1, manifest.GetProperty("schema_version").GetInt32());
        Assert.Equal("bundle", manifest.GetProperty("bundle_id").GetString());
        Assert.True(manifest.GetProperty("content_included").GetBoolean());
        Assert.Equal(2, manifest.GetProperty("evidence_index").GetArrayLength());
        Assert.Contains(bundlePath, manifest.GetProperty("delete_target_paths")[0].GetString());
        Assert.Equal("raw-otlp", manifest.GetProperty("source_inputs")[0].GetProperty("kind").GetString());

        var evidencePath = Path.Combine(bundlePath, "evidence", "diagcand-0002.json");
        using var evidenceDocument = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var evidence = evidenceDocument.RootElement;
        Assert.Equal(1, evidence.GetProperty("schema_version").GetInt32());
        Assert.Equal("diagcand-0002", evidence.GetProperty("diagnosis_candidate_id").GetString());
        Assert.Equal("trace-raw", evidence.GetProperty("trace_id").GetString());
        Assert.Equal(2, evidence.GetProperty("fragments").GetArrayLength());
        Assert.Equal("synthetic-sensitive-value", evidence.GetProperty("fragments")[0].GetProperty("value").GetString());
        Assert.Equal("synthetic prompt content should stay in bundle only", evidence.GetProperty("fragments")[1].GetProperty("value").GetString());
        Assert.Equal(64, evidence.GetProperty("fragments")[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void GenerateDiagnosisCandidates_RequiresRawWhenSensitiveContentIsIncluded()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-raw");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--include-sensitive-content", "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("--include-sensitive-content requires --raw", error.ToString());
    }

    [Fact]
    public void GenerateDiagnosisCandidates_CanReadRawStoreInput()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-raw");
        var rawPath = WriteRawOtlp(tempDirectory.Path, "trace-raw");
        var dbPath = Path.Combine(tempDirectory.Path, "raw-store.db");
        var store = new RawTelemetryStore(dbPath);
        store.CreateSchema();
        store.Insert(RawOtlpIngestor.CreateRecord(rawPath, DateTimeOffset.UtcNow));
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--raw", dbPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var jsonText = File.ReadAllText(outputPath);
        Assert.Contains("DIAG-CONTENT-ERROR-MESSAGE-V1", jsonText);
        Assert.Contains("DIAG-CONTENT-SENSITIVE-LEAK-V1", jsonText);
    }

    [Theory]
    [InlineData("safe.detail", "synthetic.user@example.com")]
    [InlineData("safe.detail", "c2VjcmV0OnRva2Vu")]
    public void GenerateDiagnosisCandidates_DetectsSensitiveTextAndBase64CredentialPredicates(string attributeKey, string attributeValue)
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-sensitive");
        var rawPath = WriteRawOtlpWithAttribute(tempDirectory.Path, "trace-sensitive", attributeKey, attributeValue);
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--raw", rawPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var jsonText = File.ReadAllText(outputPath);
        using var document = JsonDocument.Parse(jsonText);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("DIAG-CONTENT-SENSITIVE-LEAK-V1", row.GetProperty("rule_id").GetString());
        Assert.Equal("blocked", row.GetProperty("candidate_status").GetString());
        Assert.False(row.GetProperty("content_included").GetBoolean());
        Assert.DoesNotContain(attributeValue, jsonText);
    }

    [Theory]
    [InlineData("gen_ai.usage.input_tokens", "42")]
    [InlineData("gen_ai.usage.output_tokens", "100")]
    [InlineData("gen_ai.usage.total_tokens", "142")]
    public void GenerateDiagnosisCandidates_DoesNotFlagTokenUsageKeysAsSensitive(string attributeKey, string attributeValue)
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-tokens");
        var rawPath = WriteRawOtlpWithAttribute(tempDirectory.Path, "trace-tokens", attributeKey, attributeValue);
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--raw", rawPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var jsonText = File.ReadAllText(outputPath);
        Assert.DoesNotContain("DIAG-CONTENT-SENSITIVE-LEAK-V1", jsonText);
    }

    [Fact]
    public void GenerateDiagnosisCandidates_StillFlagsRealCredentialTokenKey()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = WriteMeasurements(tempDirectory.Path, "trace-authtoken");
        var rawPath = WriteRawOtlpWithAttribute(tempDirectory.Path, "trace-authtoken", "auth.token", "synthetic-credential-value");
        var outputPath = Path.Combine(tempDirectory.Path, "candidates.json");

        var exitCode = CliApplication.Run(
            ["generate-diagnosis-candidates", measurementsPath, "--raw", rawPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var jsonText = File.ReadAllText(outputPath);
        using var document = JsonDocument.Parse(jsonText);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("DIAG-CONTENT-SENSITIVE-LEAK-V1", row.GetProperty("rule_id").GetString());
        Assert.Equal("blocked", row.GetProperty("candidate_status").GetString());
    }


    private static string WriteMeasurements(string directory, string traceId)
    {
        var path = Path.Combine(directory, "measurements.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new[] { MeasurementJson(traceId, "copilot-cli", "baseline") }));
        return path;
    }

    private static string WriteRawOtlp(string directory, string traceId)
    {
        var path = Path.Combine(directory, "raw.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      {
                        "key": "service.name",
                        "value": { "stringValue": "synthetic-service" }
                      }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "{{traceId}}",
                          "spanId": "span-error",
                          "name": "execute_tool shell",
                          "status": {
                            "code": 2,
                            "message": "synthetic command failed with exit code 2"
                          },
                          "events": [
                            {
                              "name": "exception",
                              "attributes": [
                                {
                                  "key": "exception.type",
                                  "value": { "stringValue": "SyntheticException" }
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "traceId": "{{traceId}}",
                          "spanId": "span-sensitive",
                          "name": "chat",
                          "attributes": [
                            {
                              "key": "authorization.token",
                              "value": { "stringValue": "synthetic-sensitive-value" }
                            },
                            {
                              "key": "prompt.content",
                              "value": { "stringValue": "synthetic prompt content should stay in bundle only" }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        return path;
    }

    private static string WriteRawOtlpWithAttribute(string directory, string traceId, string attributeKey, string attributeValue)
    {
        var path = Path.Combine(directory, "raw-sensitive.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "resourceSpans": [
                {
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "{{traceId}}",
                          "spanId": "span-sensitive",
                          "name": "chat",
                          "attributes": [
                            {
                              "key": "{{attributeKey}}",
                              "value": { "stringValue": "{{attributeValue}}" }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        return path;
    }

    private static Dictionary<string, object?> MeasurementJson(
        string? traceId,
        string? clientKind,
        string? experimentId,
        int? errorCount = 0,
        int? toolCallCount = 0,
        string successStatus = "not-evaluated")
    {
        return new Dictionary<string, object?>
        {
            ["trace_id"] = traceId,
            ["experiment_id"] = experimentId,
            ["client_kind"] = clientKind,
            ["task_id"] = "synthetic-task",
            ["task_category"] = "bug-investigation",
            ["task_run_index"] = 1,
            ["experiment_condition"] = "baseline",
            ["prompt_version"] = "v1",
            ["agent_variant"] = "default",
            ["repo_snapshot"] = "synthetic-repo",
            ["input_tokens"] = 1,
            ["output_tokens"] = 1,
            ["total_tokens"] = 2,
            ["turn_count"] = 1,
            ["tool_call_count"] = toolCallCount,
            ["duration_ms"] = 100,
            ["error_count"] = errorCount,
            ["success_status"] = successStatus,
            ["evaluator_id"] = null,
            ["evaluation_notes"] = null,
            ["evaluated_at"] = null,
            ["unknown_spans_json"] = null,
            ["unknown_attributes_json"] = null,
            ["aggregation_notes"] = "synthetic measurement",
        };
    }

    private static string CsvRow(
        string traceId,
        string experimentId,
        string clientKind,
        string toolCallCount,
        string successStatus)
    {
        var values = MeasurementOutputWriter.Columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal);
        values["trace_id"] = traceId;
        values["experiment_id"] = experimentId;
        values["client_kind"] = clientKind;
        values["task_id"] = "synthetic-task";
        values["task_category"] = "bug-investigation";
        values["task_run_index"] = "1";
        values["experiment_condition"] = "baseline";
        values["prompt_version"] = "v1";
        values["agent_variant"] = "default";
        values["repo_snapshot"] = "synthetic-repo";
        values["input_tokens"] = "1";
        values["output_tokens"] = "1";
        values["total_tokens"] = "2";
        values["turn_count"] = "1";
        values["tool_call_count"] = toolCallCount;
        values["duration_ms"] = "100";
        values["error_count"] = "0";
        values["success_status"] = successStatus;
        values["aggregation_notes"] = "synthetic measurement";
        return string.Join(',', MeasurementOutputWriter.Columns.Select(column => values[column]));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3-diagnosis-candidates-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

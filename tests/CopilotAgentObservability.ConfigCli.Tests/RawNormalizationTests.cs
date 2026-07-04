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

        var unknownSpans = first.GetProperty("unknown_spans_json").EnumerateArray().ToArray();
        Assert.Equal(2, unknownSpans.Length);
        Assert.Equal("5555555555555555", unknownSpans[0].GetProperty("id").GetString());
        Assert.Equal("copilot.experimental.span", unknownSpans[0].GetProperty("name").GetString());
        Assert.False(unknownSpans[0].TryGetProperty("attributes", out _));
        Assert.Equal("6666666666666666", unknownSpans[1].GetProperty("id").GetString());
        Assert.False(unknownSpans[1].TryGetProperty("name", out _));

        var unknownResourceAttributes = first.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes");
        Assert.Equal("platform", unknownResourceAttributes.GetProperty("team.id").GetString());
        Assert.Equal("engineering", unknownResourceAttributes.GetProperty("department").GetString());
        Assert.Equal("synthetic-v2", unknownResourceAttributes.GetProperty("cli.wrapper.version").GetString());
        Assert.Equal("kept nested value", unknownResourceAttributes.GetProperty("metadata").GetProperty("safe.detail").GetString());
        Assert.False(unknownResourceAttributes.TryGetProperty("user.id", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("user.email", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("user.name", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("enduser.id", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("prompt.content", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("authorization.token", out _));
        Assert.False(unknownResourceAttributes.GetProperty("metadata").TryGetProperty("authorization.token", out _));
        Assert.False(unknownResourceAttributes.GetProperty("metadata").TryGetProperty("user.email", out _));
        Assert.DoesNotContain("synthetic prompt resource attribute should not leak", jsonText);
        Assert.DoesNotContain("synthetic auth token should not leak", jsonText);
        Assert.DoesNotContain("synthetic nested auth token should not leak", jsonText);
        Assert.DoesNotContain("synthetic unknown span prompt should not leak", jsonText);
        Assert.DoesNotContain("synthetic unknown span name should not leak", jsonText);
        Assert.DoesNotContain("user@example.com", jsonText);
        Assert.DoesNotContain("nested@example.com", jsonText);
        Assert.DoesNotContain("Synthetic User", jsonText);
        Assert.DoesNotContain("synthetic-end-user", jsonText);
    }

    [Fact]
    public void NormalizeRaw_WritesCsvWithFixedColumnsAndBlankMissingValues()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("minimal.json", MinimalRawJson());
        var csvPath = Path.Combine(tempDirectory.Path, "measurements.csv");

        var exitCode = CliApplication.Run(
            ["normalize-raw", inputPath, "--csv", csvPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var lines = File.ReadAllLines(csvPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal(string.Join(',', MeasurementOutputWriter.Columns), lines[0]);
        var values = ParseCsvLine(lines[1]);
        Assert.Equal(MeasurementOutputWriter.Columns.Length, values.Count);
        Assert.Equal("99999999999999999999999999999999", CsvValue(values, "trace_id"));
        Assert.Equal(string.Empty, CsvValue(values, "experiment_id"));
        Assert.Equal("copilot-cli", CsvValue(values, "client_kind"));
        Assert.Equal(string.Empty, CsvValue(values, "task_id"));
        Assert.Equal(string.Empty, CsvValue(values, "task_run_index"));
        Assert.Equal("0", CsvValue(values, "turn_count"));
        Assert.Equal("0", CsvValue(values, "tool_call_count"));
        Assert.Equal(string.Empty, CsvValue(values, "duration_ms"));
        Assert.Equal("0", CsvValue(values, "error_count"));
        Assert.Equal("not-evaluated", CsvValue(values, "success_status"));
        Assert.Equal(string.Empty, CsvValue(values, "unknown_spans_json"));
        Assert.Equal(string.Empty, CsvValue(values, "unknown_attributes_json"));
    }

    [Fact]
    public void NormalizeRaw_WritesJsonWithFixedSchemaAndNullMissingValues()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("minimal.json", MinimalRawJson());
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var exitCode = CliApplication.Run(
            ["normalize-raw", inputPath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(
            MeasurementOutputWriter.Columns.Order(StringComparer.Ordinal),
            row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var column in MeasurementOutputWriter.Columns)
        {
            Assert.True(row.TryGetProperty(column, out _), $"Expected JSON column '{column}'.");
        }

        Assert.Equal(JsonValueKind.Null, row.GetProperty("experiment_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("task_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("task_run_index").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("input_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("duration_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("unknown_spans_json").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("unknown_attributes_json").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("evaluator_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("evaluation_notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("evaluated_at").ValueKind);
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
    public void NormalizeRaw_ReadsAllRawStoreRecordsUsingPayloadJson()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);
        store.CreateSchema();
        store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "metadata-trace-should-not-be-used-1",
            ReceivedAt: DateTimeOffset.UtcNow,
            ResourceAttributesJson: """{"client.kind":"metadata-should-not-be-used"}""",
            PayloadJson: MinimalRawJson("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "copilot-cli")));
        store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "metadata-trace-should-not-be-used-2",
            ReceivedAt: DateTimeOffset.UtcNow,
            ResourceAttributesJson: """{"client.kind":"metadata-should-not-be-used"}""",
            PayloadJson: MinimalRawJson("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "vscode-copilot-chat")));
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var exitCode = CliApplication.Run(
            ["normalize-raw", tempDirectory.DatabasePath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", rows[0].GetProperty("trace_id").GetString());
        Assert.Equal("copilot-cli", rows[0].GetProperty("client_kind").GetString());
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", rows[1].GetProperty("trace_id").GetString());
        Assert.Equal("vscode-copilot-chat", rows[1].GetProperty("client_kind").GetString());
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

    [Fact]
    public void NormalizeRaw_RemovesAdditionalIdentityAndContentMarkersFromAuxiliaryJson()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("unsafe-auxiliary.json", UnsafeAuxiliaryRawJson());
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var exitCode = CliApplication.Run(
            ["normalize-raw", inputPath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var jsonText = File.ReadAllText(jsonPath);
        using var document = JsonDocument.Parse(jsonText);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        var unknownResourceAttributes = row.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes");
        Assert.Equal("safe value", unknownResourceAttributes.GetProperty("safe.detail").GetString());
        Assert.False(unknownResourceAttributes.TryGetProperty("user_id", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("userId", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("username", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("email", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("metadata", out _));

        var unknownSpans = row.GetProperty("unknown_spans_json").EnumerateArray().ToArray();
        Assert.Equal(3, unknownSpans.Length);
        Assert.All(unknownSpans, span => Assert.False(span.TryGetProperty("name", out _)));
        Assert.DoesNotContain("response:", jsonText);
        Assert.DoesNotContain("content:", jsonText);
        Assert.DoesNotContain("tool arguments:", jsonText);
        Assert.DoesNotContain("user@example.com", jsonText);
        Assert.DoesNotContain("synthetic-user-id", jsonText);
        Assert.DoesNotContain("syntheticUserId", jsonText);
        Assert.DoesNotContain("synthetic-user-name", jsonText);
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "raw-otlp.synthetic.json");
    }

    private static string MinimalRawJson(
        string traceId = "99999999999999999999999999999999",
        string clientKind = "copilot-cli")
    {
        return $$"""
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "{{clientKind}}" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "{{traceId}}",
                          "spanId": "aaaaaaaaaaaaaaaa",
                          "name": "invoke_agent"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
    }

    private static string UnsafeAuxiliaryRawJson()
    {
        return """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "copilot-cli" } },
                      { "key": "safe.detail", "value": { "stringValue": "safe value" } },
                      { "key": "user_id", "value": { "stringValue": "synthetic-user-id" } },
                      { "key": "userId", "value": { "stringValue": "syntheticUserId" } },
                      { "key": "username", "value": { "stringValue": "synthetic-user-name" } },
                      { "key": "email", "value": { "stringValue": "user@example.com" } },
                      {
                        "key": "metadata",
                        "value": {
                          "kvlistValue": {
                            "values": [
                              { "key": "user_id", "value": { "stringValue": "nested-synthetic-user-id" } }
                            ]
                          }
                        }
                      }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "77777777777777777777777777777777",
                          "spanId": "7777777777777771",
                          "name": "response: synthetic response content should not leak"
                        },
                        {
                          "traceId": "77777777777777777777777777777777",
                          "spanId": "7777777777777772",
                          "name": "content: synthetic content should not leak"
                        },
                        {
                          "traceId": "77777777777777777777777777777777",
                          "spanId": "7777777777777773",
                          "name": "tool arguments: synthetic args should not leak"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
    }

    private static string CsvValue(IReadOnlyList<string> values, string column)
    {
        return values[Array.IndexOf(MeasurementOutputWriter.Columns, column)];
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        values.Add(builder.ToString());
        return values;
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

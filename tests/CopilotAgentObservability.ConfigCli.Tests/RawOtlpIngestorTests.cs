using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawOtlpIngestorTests
{
    [Fact]
    public void CreateRecordFromPayloadJson_PreservesExactPayloadStringAndExtractsMetadata()
    {
        var payloadJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "copilot-cli" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "11111111111111111111111111111111"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var receivedAt = new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero);

        var record = RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, receivedAt);

        Assert.Equal(RawTelemetrySources.RawOtlp, record.Source);
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Equal(receivedAt, record.ReceivedAt);
        Assert.Equal(payloadJson, record.PayloadJson);

        using var attributes = JsonDocument.Parse(record.ResourceAttributesJson!);
        Assert.Equal("copilot-cli", attributes.RootElement.GetProperty("client.kind").GetString());
    }

    [Fact]
    public void CreateRecord_ExtractsTraceIdAndResourceAttributes()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteJson("raw.json", """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "copilot-cli" } },
                      { "key": "experiment.id", "value": { "stringValue": "baseline" } },
                      { "key": "task.run_index", "value": { "intValue": "2" } },
                      { "key": "synthetic.flag", "value": { "boolValue": true } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "11111111111111111111111111111111",
                          "spanId": "2222222222222222",
                          "name": "synthetic.agent.invocation"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        var receivedAt = new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero);

        var record = RawOtlpIngestor.CreateRecord(inputPath, receivedAt);

        Assert.Equal(RawTelemetrySources.RawOtlp, record.Source);
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Equal(receivedAt, record.ReceivedAt);
        Assert.Equal(File.ReadAllText(inputPath), record.PayloadJson);

        using var attributes = JsonDocument.Parse(record.ResourceAttributesJson!);
        Assert.Equal("copilot-cli", attributes.RootElement.GetProperty("client.kind").GetString());
        Assert.Equal("baseline", attributes.RootElement.GetProperty("experiment.id").GetString());
        Assert.Equal(2, attributes.RootElement.GetProperty("task.run_index").GetInt64());
        Assert.True(attributes.RootElement.GetProperty("synthetic.flag").GetBoolean());
    }

    [Fact]
    public void CreateRecord_UsesFirstNonEmptyTraceIdAcrossResourceSpans()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteJson("raw.json", """
            {
              "resourceSpans": [
                {
                  "scopeSpans": [
                    {
                      "spans": [
                        { "traceId": "", "spanId": "2222222222222222" }
                      ]
                    }
                  ]
                },
                {
                  "scopeSpans": [
                    {
                      "spans": [
                        { "traceId": "33333333333333333333333333333333", "spanId": "4444444444444444" }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var record = RawOtlpIngestor.CreateRecord(inputPath, DateTimeOffset.UtcNow);

        Assert.Equal("33333333333333333333333333333333", record.TraceId);
    }

    [Fact]
    public void CreateRecord_AllowsMissingTraceIdAndResourceAttributes()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteJson("raw.json", """
            {
              "resourceSpans": [
                {
                  "scopeSpans": [
                    {
                      "spans": [
                        { "spanId": "2222222222222222" }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var record = RawOtlpIngestor.CreateRecord(inputPath, DateTimeOffset.UtcNow);

        Assert.Null(record.TraceId);
        Assert.Null(record.ResourceAttributesJson);
    }

    [Fact]
    public void CreateRecord_PreservesArrayAndKeyValueAttributeShapes()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteJson("raw.json", """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      {
                        "key": "synthetic.tags",
                        "value": {
                          "arrayValue": {
                            "values": [
                              { "stringValue": "one" },
                              { "stringValue": "two" }
                            ]
                          }
                        }
                      },
                      {
                        "key": "synthetic.nested",
                        "value": {
                          "kvlistValue": {
                            "values": [
                              { "key": "enabled", "value": { "boolValue": true } }
                            ]
                          }
                        }
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var record = RawOtlpIngestor.CreateRecord(inputPath, DateTimeOffset.UtcNow);

        using var attributes = JsonDocument.Parse(record.ResourceAttributesJson!);
        Assert.Equal("one", attributes.RootElement.GetProperty("synthetic.tags")[0].GetString());
        Assert.Equal("two", attributes.RootElement.GetProperty("synthetic.tags")[1].GetString());
        Assert.True(attributes.RootElement.GetProperty("synthetic.nested").GetProperty("enabled").GetBoolean());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3-raw-otlp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteJson(string fileName, string content)
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

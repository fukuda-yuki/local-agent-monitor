using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawLocalReceiverHandlerTests
{
    [Fact]
    public void Handle_PersistsJsonTraceRequest()
    {
        using var tempDirectory = new ReceiverTempDirectory();
        var receivedAt = new DateTimeOffset(2026, 6, 21, 1, 2, 3, TimeSpan.Zero);

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes(JsonTracePayload()),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: receivedAt));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.NotNull(response.RawRecordId);
        Assert.DoesNotContain("11111111111111111111111111111111", response.Body);
        using var responseJson = JsonDocument.Parse(response.Body);
        Assert.True(responseJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(response.RawRecordId, responseJson.RootElement.GetProperty("rawRecordId").GetInt64());

        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal(response.RawRecordId, record.Id);
        Assert.Equal(RawTelemetrySources.RawOtlp, record.Source);
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Equal(receivedAt, record.ReceivedAt);
        Assert.Equal(JsonTracePayload(), record.PayloadJson);
    }

    [Fact]
    public void Handle_PersistsProtobufTraceRequestAfterConversion()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/x-protobuf",
            Body: OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest(),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.RawRecordId);

        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        using var payloadJson = JsonDocument.Parse(record.PayloadJson);
        Assert.Equal(
            "vscode-copilot-chat",
            payloadJson.RootElement
                .GetProperty("resourceSpans")[0]
                .GetProperty("resource")
                .GetProperty("attributes")[0]
                .GetProperty("value")
                .GetProperty("stringValue")
                .GetString());
    }

    [Fact]
    public void Handle_RejectsInvalidJsonTraceRequestWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes("{"),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(400, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
        using var responseJson = JsonDocument.Parse(response.Body);
        Assert.False(responseJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("invalid_payload", responseJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Handle_RejectsJsonTraceRequestWithoutSpansWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(400, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Handle_RejectsEmptyProtobufTraceRequestWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/x-protobuf",
            Body: [],
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(400, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Handle_RejectsMalformedProtobufTraceRequestWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/x-protobuf",
            Body: [0x0A, 0x05, 0x01],
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(400, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Handle_ReturnsPersistenceFailureWhenDatabaseCannotBeWritten()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes(JsonTracePayload()),
            DatabasePath: tempDirectory.Path,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(500, response.StatusCode);
        Assert.Null(response.RawRecordId);
        using var responseJson = JsonDocument.Parse(response.Body);
        Assert.False(responseJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("persistence_failed", responseJson.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(tempDirectory.Path, response.Body);
    }

    [Fact]
    public void Handle_RejectsGetTraceRequestWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "GET",
            Path: "/v1/traces",
            ContentType: null,
            Body: [],
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(405, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Handle_RejectsUnsupportedSignalWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/metrics",
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes(JsonTracePayload()),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(404, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Handle_RejectsUnsupportedContentTypeWithoutPersisting()
    {
        using var tempDirectory = new ReceiverTempDirectory();

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "text/plain",
            Body: Encoding.UTF8.GetBytes(JsonTracePayload()),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        Assert.Equal(415, response.StatusCode);
        Assert.Null(response.RawRecordId);
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    private static string JsonTracePayload()
    {
        return """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "vscode-copilot-chat" } },
                      { "key": "experiment.id", "value": { "stringValue": "baseline" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "11111111111111111111111111111111",
                          "spanId": "2222222222222222",
                          "name": "chat gpt-4o",
                          "startTimeUnixNano": "1000000000",
                          "endTimeUnixNano": "1500000000",
                          "attributes": [
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "10" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "5" } }
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
}

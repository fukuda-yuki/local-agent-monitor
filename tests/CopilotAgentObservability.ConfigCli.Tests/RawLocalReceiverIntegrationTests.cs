using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawLocalReceiverIntegrationTests
{
    [Fact]
    public void HandleProtobufTrace_NormalizeRawWritesExpectedMeasurementRow()
    {
        using var tempDirectory = new ReceiverTempDirectory();
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var response = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: "POST",
            Path: "/v1/traces",
            ContentType: "application/x-protobuf",
            Body: OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest(),
            DatabasePath: tempDirectory.DatabasePath,
            ReceivedAt: new DateTimeOffset(2026, 6, 21, 1, 2, 3, TimeSpan.Zero)));

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.RawRecordId);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(
            ["normalize-raw", tempDirectory.DatabasePath, "--json", measurementsPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Normalized 1 raw measurement row(s).", output.ToString());

        using var document = JsonDocument.Parse(File.ReadAllText(measurementsPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("11111111111111111111111111111111", row.GetProperty("trace_id").GetString());
        Assert.Equal("vscode-copilot-chat", row.GetProperty("client_kind").GetString());
        Assert.Equal("baseline", row.GetProperty("experiment_id").GetString());
        Assert.Equal(10, row.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, row.GetProperty("output_tokens").GetInt32());
        Assert.Equal(15, row.GetProperty("total_tokens").GetInt32());
        Assert.Equal(500, row.GetProperty("duration_ms").GetInt32());
    }
}

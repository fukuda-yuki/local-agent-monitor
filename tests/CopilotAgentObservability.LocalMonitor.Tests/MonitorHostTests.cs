using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorHostTests
{
    [Fact]
    public async Task PostTraces_ValidJsonPersistsRawRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"accepted\":true", await response.Content.ReadAsStringAsync());
        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Contains("\"client.kind\"", record.PayloadJson);
    }

    [Fact]
    public async Task PostTraces_ValidProtobufPersistsRawRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        using var content = new ByteArrayContent(OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var response = await host.Client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
    }

    [Fact]
    public async Task PostTraces_BodyAtLimitIsAcceptedAndOneByteOverLimitIsRejected()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var payloadAtLimit = PaddedTraceJson(512);
        await using var host = await StartHostAsync(tempDirectory, maxRequestBodyBytes: Encoding.UTF8.GetByteCount(payloadAtLimit));

        var accepted = await host.Client.PostAsync("/v1/traces", JsonContent(payloadAtLimit));
        var rejected = await host.Client.PostAsync("/v1/traces", JsonContent(payloadAtLimit + " "));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.Contains("request_too_large", await rejected.Content.ReadAsStringAsync());
        Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
    }

    [Theory]
    [InlineData("/v1/metrics")]
    [InlineData("/v1/logs")]
    [InlineData("/unexpected")]
    public async Task UnsupportedPath_Returns404AndWritesNoRecord(string path)
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.PostAsync(path, JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("unsupported_endpoint", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task GetTraces_Returns405AndWritesNoRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.GetAsync("/v1/traces");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("method_not_allowed", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_UnsupportedContentTypeReturns415AndWritesNoRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        using var content = new StringContent(ValidTraceJson(), Encoding.UTF8, "text/plain");
        var response = await host.Client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("unsupported_content_type", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_InvalidPayloadReturns400AndHostKeepsServing()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var invalid = await host.Client.PostAsync("/v1/traces", JsonContent("""{"resourceSpans":[]}"""));
        var valid = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("invalid_payload", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
    }

    [Fact]
    public async Task NonLoopbackHostHeaderIsRejected()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = JsonContent(ValidTraceJson()),
            Headers = { Host = "example.com" },
        };

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_host", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_ExternalReaderTransactionDoesNotBlockPersistedWrite()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tempDirectory.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        ExecuteSql(connection, "BEGIN;");
        ExecuteSql(connection, "SELECT COUNT(*) FROM raw_records;");

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ExecuteSql(connection, "ROLLBACK;");
        Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
    }

    [Theory]
    [InlineData("/traces/1/raw")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task DeferredRoutesReturn404(string path)
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PortAlreadyBoundRunReturnsDeterministicStartupError()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var port = GetFreePort();
        await using var host = await StartHostAsync(tempDirectory, port: port);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MonitorHost.RunAsync(
            new MonitorOptions(tempDirectory.DatabasePath, $"http://127.0.0.1:{port}", EnableRawView: false, MaxRequestBodyBytes: 31_457_280),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("failed to start local monitor: port is already in use", error.ToString());
        Assert.DoesNotContain(nameof(IOException), error.ToString());
    }

    private static async Task<RunningMonitorForTest> StartHostAsync(
        MonitorTempDirectory tempDirectory,
        int? port = null,
        int maxRequestBodyBytes = 31_457_280)
    {
        port ??= GetFreePort();
        var url = $"http://127.0.0.1:{port}";
        var options = new MonitorOptions(tempDirectory.DatabasePath, url, EnableRawView: false, maxRequestBodyBytes);
        var app = MonitorHost.Build(options);
        await app.StartAsync();
        return new RunningMonitorForTest(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string ValidTraceJson()
    {
        return """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222","name":"chat gpt-4o"}]}]}]}
        """;
    }

    private static string PaddedTraceJson(int byteCount)
    {
        const string prefix = "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"11111111111111111111111111111111\",\"spanId\":\"2222222222222222\",\"name\":\"chat\",\"attributes\":[{\"key\":\"padding\",\"value\":{\"stringValue\":\"";
        const string suffix = "\"}}]}]}]}]}";
        var paddingLength = byteCount - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        Assert.True(paddingLength >= 0, "Requested payload size is too small for the valid trace fixture.");
        return prefix + new string('x', paddingLength) + suffix;
    }

    private static void AssertNoRecords(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        Assert.Empty(new RawTelemetryStore(databasePath).ListRecords());
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void ExecuteSql(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private sealed class RunningMonitorForTest(IAsyncDisposable app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}

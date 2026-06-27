using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
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

    [Fact]
    public async Task HealthLive_Returns200()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"live\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthReady_Returns200ReadyWhenHealthyAndCaughtUp()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        // The monitor is ready only after the projection worker's first successful
        // status read (it is not ready on a stale zero-lag snapshot), so poll.
        var body = await WaitForReadyBodyAsync(host);

        Assert.Contains("\"status\":\"ready\"", body);
        Assert.Contains("\"migration_complete\":true", body);
        Assert.Contains("\"writer_running\":true", body);
        Assert.Contains("\"projection_worker_running\":true", body);
        Assert.Contains("\"degraded_reasons\":[]", body);
    }

    [Fact]
    public async Task HealthReady_Returns503NotReadyWhenProjectionWorkerDisabled()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { StartProjectionWorker = false });

        var response = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"not_ready\"", body);
        Assert.Contains("projection_worker_missing", body);
        Assert.Contains("\"projection_worker_running\":false", body);
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
            new MonitorOptions(tempDirectory.DatabasePath, $"http://127.0.0.1:{port}", SanitizedOnly: false, MaxRequestBodyBytes: 31_457_280),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("failed to start local monitor: port is already in use", error.ToString());
        Assert.DoesNotContain(nameof(IOException), error.ToString());
    }

    [Fact]
    public async Task PostTraces_ResponseIsWithheldUntilCommitAck()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var writer = new GatedRawWriter();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Writer = writer });

        var postTask = host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
        await writer.Entered;

        Assert.False(postTask.IsCompleted);

        writer.Release();
        var response = await postTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"accepted\":true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostTraces_FullQueueReturns503AndWritesNoRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(SyntheticRecord(), out _));
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Queue = queue, StartWriter = false });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("queue_full", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_DuringShutdownReturns503ShuttingDownAndWritesNoRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var queue = new IngestionQueue(capacity: 4);
        queue.CompleteAdding();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Queue = queue, StartWriter = false });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("shutting_down", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_CommitTimeoutReturns504AndWritesNoRecord()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions
            {
                StartWriter = false,
                CommitTimeout = TimeSpan.FromMilliseconds(50),
            });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("commit_timeout", await response.Content.ReadAsStringAsync());
        AssertNoRecords(tempDirectory.DatabasePath);
    }

    [Fact]
    public async Task PostTraces_CommitTimeout_StartsTheReadinessStallWindow()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var health = new MonitorHealthState();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions
            {
                StartWriter = false,
                CommitTimeout = TimeSpan.FromMilliseconds(50),
                Health = health,
            });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.NotNull(health.Snapshot().UnableToCommitSince);
    }

    [Fact]
    public async Task PostTraces_PersistenceBusyReturns503()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Writer = new ThrowingRawWriter(busy: true) });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("persistence_busy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostTraces_NonBusyPersistenceFailureReturns500()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Writer = new ThrowingRawWriter(busy: false) });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("persistence_failed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDetailRoute_IsAbsentUnderSanitizedOnly()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory, sanitizedOnly: true);

        var response = await host.Client.GetAsync("/traces/1/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentExternalReadTransaction_DoesNotLoseMonitorWrites()
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

        for (var i = 0; i < 5; i++)
        {
            var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        ExecuteSql(connection, "ROLLBACK;");
        Assert.Equal(5, new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords().Count);
    }

    [Fact]
    public async Task ErrorResponses_DoNotLeakDbPathUserNameOrExceptionText()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { Writer = new ThrowingRawWriter(busy: false) });

        var failed = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
        var body = await failed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("persistence_failed", body);
        Assert.DoesNotContain(tempDirectory.DatabasePath, body);
        Assert.DoesNotContain(Environment.UserName, body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("Sqlite", body);
    }

    [Fact]
    public async Task ProjectionWorker_CatchesUpRowsIngestedWhileProjectionWasNotRunning()
    {
        using var tempDirectory = new MonitorTempDirectory();

        // Ingest with the projection worker OFF, so raw_records accrue unprojected.
        await using (var ingestOnly = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { StartProjectionWorker = false }))
        {
            for (var i = 0; i < 3; i++)
            {
                var response = await ingestOnly.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // A fresh host on the same DB catches up the backlog via its projection worker.
        await using var monitor = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(3, await WaitForIngestionProjectionCountAsync(monitor, expected: 3));
    }

    [Fact]
    public async Task ProjectionWritesSucceedDuringConcurrentExternalReadTransaction()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var monitor = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tempDirectory.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        ExecuteSql(connection, "BEGIN;");
        ExecuteSql(connection, "SELECT COUNT(*) FROM raw_records;");

        for (var i = 0; i < 3; i++)
        {
            var response = await monitor.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var count = await WaitForIngestionProjectionCountAsync(monitor, expected: 3);
        ExecuteSql(connection, "ROLLBACK;");
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ReadinessBody_DoesNotLeakDbPathOrUserName()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var body = await (await host.Client.GetAsync("/health/ready")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(tempDirectory.DatabasePath, body);
        Assert.DoesNotContain(Environment.UserName, body);
    }

    private static async Task<string> WaitForReadyBodyAsync(RunningMonitorForTest host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string body = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var response = await host.Client.GetAsync("/health/ready");
            body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK && body.Contains("\"status\":\"ready\""))
            {
                return body;
            }

            await Task.Delay(25);
        }

        return body;
    }

    private static async Task<int> WaitForIngestionProjectionCountAsync(RunningMonitorForTest host, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var body = await host.Client.GetStringAsync("/api/monitor/ingestions?limit=200");
            using var document = JsonDocument.Parse(body);
            var count = document.RootElement.GetProperty("items").GetArrayLength();
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(25);
        }

        return -1;
    }

    private static async Task<RunningMonitorForTest> StartHostAsync(
        MonitorTempDirectory tempDirectory,
        int? port = null,
        int maxRequestBodyBytes = 31_457_280,
        MonitorHostTestOptions? testOptions = null,
        bool sanitizedOnly = false)
    {
        port ??= GetFreePort();
        var url = $"http://127.0.0.1:{port}";
        var options = new MonitorOptions(tempDirectory.DatabasePath, url, SanitizedOnly: sanitizedOnly, maxRequestBodyBytes);
        var app = testOptions is null ? MonitorHost.Build(options) : MonitorHost.Build(options, testOptions);
        await app.StartAsync();
        return new RunningMonitorForTest(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static RawTelemetryRecord SyntheticRecord() =>
        new(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace",
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");

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

    private sealed class RunningMonitorForTest(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            // Gracefully stop hosted services (incl. the polling projection worker)
            // so no SQLite connection is open when the temp DB directory is deleted.
            try
            {
                await app.StopAsync();
            }
            catch
            {
                // Ignore stop faults during teardown.
            }

            await app.DisposeAsync();
        }
    }

    private sealed class ThrowingRawWriter : IRawTelemetryWriter
    {
        private readonly bool busy;

        public ThrowingRawWriter(bool busy)
        {
            this.busy = busy;
        }

        public void EnsureSchema()
        {
        }

        public long Insert(RawTelemetryRecord record)
        {
            if (busy)
            {
                throw new PersistenceBusyException();
            }

            throw new PersistenceFailedException();
        }
    }

    private sealed class GatedRawWriter : IRawTelemetryWriter
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim gate = new(initialState: false);
        private long nextId;

        public Task Entered => entered.Task;

        public void Release() => gate.Set();

        public void EnsureSchema()
        {
        }

        public long Insert(RawTelemetryRecord record)
        {
            entered.TrySetResult();
            gate.Wait();
            return Interlocked.Increment(ref nextId);
        }
    }
}

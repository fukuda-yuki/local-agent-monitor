using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.ProposalApply;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorHostTests
{
    [Fact]
    public void Build_MigratesAndRegistersDedicatedSessionStoreSynchronously()
    {
        using var tempDirectory = new MonitorTempDirectory();

        using var app = MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false });

        var store = app.Services.GetRequiredService<ISessionStore>();
        Assert.Null(store.GetProjectionState("host-startup-probe"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDirectory.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version WHERE component='session';";
        Assert.Equal(11L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Build_WhenSessionMigrationFails_FailsHostConstruction()
    {
        using var tempDirectory = new MonitorTempDirectory();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDirectory.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // A database from a future schema version must not be opened by this host.
            command.CommandText = "CREATE TABLE schema_version(component TEXT PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO schema_version VALUES('session', 12);";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false }));
    }

    [Fact]
    public void Build_WhenRuntimeStateUpsertFails_FailsHostConstruction()
    {
        using var tempDirectory = new MonitorTempDirectory();
        new SqliteMonitorRuntimeStateStore(
            tempDirectory.DatabasePath,
            timeProvider: null,
            RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDirectory.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TRIGGER fail_runtime_state_upsert
                BEFORE INSERT ON monitor_runtime_state
                BEGIN
                    SELECT RAISE(FAIL, 'runtime-state-upsert failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<SqliteException>(() => MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false }));

        Assert.Contains("runtime-state-upsert failure", exception.Message, StringComparison.Ordinal);
        Assert.Null(new SqliteMonitorRuntimeStateStore(tempDirectory.DatabasePath).Get());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_WithUnresolvedApplyRecoveryRoot_FailsClosedWithoutWritingPartialTransaction(bool omitRoot)
    {
        using var tempDirectory = new MonitorTempDirectory();
        var configuredRootPath = Path.Combine(tempDirectory.Path, "configured");
        var changedRootPath = Path.Combine(tempDirectory.Path, "changed");
        Directory.CreateDirectory(configuredRootPath);
        Directory.CreateDirectory(changedRootPath);
        var firstTarget = Path.Combine(configuredRootPath, "one.txt");
        var secondTarget = Path.Combine(configuredRootPath, "two.txt");
        File.WriteAllText(firstTarget, "one");
        File.WriteAllText(secondTarget, "two");
        var configuredRoot = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, configuredRootPath);
        var runtimePath = Path.Combine(Path.GetDirectoryName(tempDirectory.DatabasePath)!, "proposal-apply");
        var crashing = new ProposalApplyTransaction(runtimePath, [configuredRoot], point =>
        {
            if (point == "after_atomic_replace:0") throw new ApplyTransactionCrashException();
        });

        Assert.Throws<ApplyTransactionCrashException>(() => crashing.Apply(
            Guid.CreateVersion7(),
            [
                ApplyTarget.Create(configuredRoot, "one.txt", "one", "ONE"),
                ApplyTarget.Create(configuredRoot, "two.txt", "two", "TWO"),
            ]));

        var startupRoots = omitRoot
            ? Array.Empty<ConfiguredApplyRoot>()
            : [ConfiguredApplyRoot.Create(ApplyRootKind.Repository, changedRootPath)];
        var firstWriteTime = File.GetLastWriteTimeUtc(firstTarget);
        var secondWriteTime = File.GetLastWriteTimeUtc(secondTarget);

        Assert.Throws<ApplyRecoveryException>(() => MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280, ApplyRoots: startupRoots),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false }));

        Assert.Equal("ONE", File.ReadAllText(firstTarget));
        Assert.Equal("two", File.ReadAllText(secondTarget));
        Assert.Equal(firstWriteTime, File.GetLastWriteTimeUtc(firstTarget));
        Assert.Equal(secondWriteTime, File.GetLastWriteTimeUtc(secondTarget));
        Assert.Single(Directory.EnumerateFiles(runtimePath, "journal.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Build_WithSameApplyRecoveryRoot_RestoresOriginalsAndRegistersOneServiceInstance()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var rootPath = Path.Combine(tempDirectory.Path, "configured");
        Directory.CreateDirectory(rootPath);
        var firstTarget = Path.Combine(rootPath, "one.txt");
        var secondTarget = Path.Combine(rootPath, "two.txt");
        File.WriteAllText(firstTarget, "one");
        File.WriteAllText(secondTarget, "two");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var runtimePath = Path.Combine(Path.GetDirectoryName(tempDirectory.DatabasePath)!, "proposal-apply");
        var crashing = new ProposalApplyTransaction(runtimePath, [root], point =>
        {
            if (point == "after_atomic_replace:0") throw new ApplyTransactionCrashException();
        });

        Assert.Throws<ApplyTransactionCrashException>(() => crashing.Apply(
            Guid.CreateVersion7(),
            [
                ApplyTarget.Create(root, "one.txt", "one", "ONE"),
                ApplyTarget.Create(root, "two.txt", "two", "TWO"),
            ]));

        using var app = MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280, ApplyRoots: [root]),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false });

        var service = app.Services.GetRequiredService<ProposalApplyService>();
        Assert.Same(service, app.Services.GetRequiredService<ProposalApplyService>());
        Assert.Equal(rootPath, Assert.Single(service.Roots).CanonicalPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("one", File.ReadAllText(firstTarget));
        Assert.Equal("two", File.ReadAllText(secondTarget));
    }

    [Fact]
    public async Task PostTraces_ValidJsonPersistsCanonicalPayloadWithoutStructuralInventory()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"accepted\":true", await response.Content.ReadAsStringAsync());
        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Equal(ValidTraceJson(), record.PayloadJson);
        Assert.DoesNotContain("StructuralInventory", record.PayloadJson, StringComparison.Ordinal);
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
    public async Task PostTraces_MalformedPayloadReturns400AndHostKeepsServing()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        var invalid = await host.Client.PostAsync("/v1/traces", JsonContent("""{"resourceSpans":["""));
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
    public async Task TestHostHelper_BindsDynamicLoopbackPort()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(tempDirectory);

        Assert.StartsWith("http://127.0.0.1:", host.Url, StringComparison.Ordinal);
        Assert.False(host.Url.EndsWith(":0", StringComparison.Ordinal));
        Assert.Equal(new Uri(host.Url), host.Client.BaseAddress);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task PortAlreadyBoundRunReturnsDeterministicStartupError()
    {
        using var tempDirectory = new MonitorTempDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
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
        var writer = new GatedCommitStore();
        await using var host = await StartHostAsync(
            tempDirectory,
            testOptions: new MonitorHostTestOptions { IngestionCommitStore = writer });

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
        Assert.True(queue.TryEnqueue(SyntheticBatch(), out _));
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
            testOptions: new MonitorHostTestOptions { IngestionCommitStore = new ThrowingCommitStore(busy: true) });

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
            testOptions: new MonitorHostTestOptions { IngestionCommitStore = new ThrowingCommitStore(busy: false) });

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
            testOptions: new MonitorHostTestOptions { IngestionCommitStore = new ThrowingCommitStore(busy: false) });

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

    private static async Task<string> WaitForReadyBodyAsync(RunningMonitorHost host)
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

    private static async Task<int> WaitForIngestionProjectionCountAsync(RunningMonitorHost host, int expected)
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

    private static Task<RunningMonitorHost> StartHostAsync(
        MonitorTempDirectory tempDirectory,
        int maxRequestBodyBytes = 31_457_280,
        MonitorHostTestOptions? testOptions = null,
        bool sanitizedOnly = false)
    {
        return MonitorTestHost.StartAsync(
            tempDirectory,
            sanitizedOnly: sanitizedOnly,
            maxRequestBodyBytes: maxRequestBodyBytes,
            testOptions: testOptions);
    }

    private static ValidatedIngestionBatch SyntheticBatch()
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace",
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");
        var inventory = OtlpJsonStructuralWalker.Build(
            """{"resourceSpans":[{"scopeSpans":[{"spans":[{}]}]}]}""",
            DateTimeOffset.UnixEpoch);
        var observation = SourceObservationBatchDraft.Create(
            "batch-trace", "raw-otlp", null, "raw-otlp", "1", inventory,
            SourceCompatibilityEvaluator.Assess(
                "raw-otlp", null, inventory, 1, VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Unsupported, DateTimeOffset.UnixEpoch);
        return ValidatedIngestionBatch.Create(record, observation);
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

    private static void ExecuteSql(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private sealed class ThrowingCommitStore : IIngestionCommitStore
    {
        private readonly bool busy;

        public ThrowingCommitStore(bool busy)
        {
            this.busy = busy;
        }

        public CommittedIngestionIds Commit(ValidatedIngestionBatch batch)
        {
            if (busy)
            {
                throw new IngestionCommitBusyException();
            }

            throw new IngestionCommitFailedException();
        }
    }

    private sealed class GatedCommitStore : IIngestionCommitStore
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim gate = new(initialState: false);
        private long nextId;

        public Task Entered => entered.Task;

        public void Release() => gate.Set();

        public CommittedIngestionIds Commit(ValidatedIngestionBatch batch)
        {
            entered.TrySetResult();
            gate.Wait();
            var rawRecordId = Interlocked.Increment(ref nextId);
            return new CommittedIngestionIds(rawRecordId, rawRecordId + 100);
        }
    }
}

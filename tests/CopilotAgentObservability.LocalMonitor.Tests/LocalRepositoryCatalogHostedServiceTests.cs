using CopilotAgentObservability.LocalMonitor.Repositories;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryCatalogHostedServiceTests
{
    [Fact]
    public async Task RawDefault_SinglePassDiscoversTwoProjectedRowsAndClaimsExactlyOne()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        using var temp = new MonitorTempDirectory { TimeProvider = time };
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost());
        var rawStore = new RawTelemetryStore(
            temp.DatabasePath,
            app.Services.GetRequiredService<RetentionCatalogContext>(),
            time,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var firstRawId = rawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "11111111111111111111111111111111",
            time.GetUtcNow(),
            null,
            "{}"));
        var secondRawId = rawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "22222222222222222222222222222222",
            time.GetUtcNow(),
            null,
            "{}"));
        var unprojectedRawId = rawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "33333333333333333333333333333333",
            time.GetUtcNow(),
            null,
            "{}"));
        using (var connection = Open(temp.DatabasePath))
        {
            InsertProjectedSpan(connection, firstRawId, "11111111111111111111111111111111");
            InsertProjectedSpan(connection, secondRawId, "22222222222222222222222222222222");
        }

        var worker = Assert.Single(app.Services.GetServices<IHostedService>()
            .OfType<LocalRepositoryCatalogHostedService>());

        await worker.RunOnePassAsync(CancellationToken.None);

        using var verify = Open(temp.DatabasePath);
        Assert.Equal(2, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(verify, $"SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE raw_record_id={unprojectedRawId};"));
        Assert.Equal(1, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE attempt_count=1;"));
        Assert.Equal(1, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE attempt_count=0;"));
        Assert.Equal(1, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='failed_terminal';"));
        Assert.Equal(1, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='pending';"));
    }

    [Fact]
    public async Task RawDefault_StartAndStop_HonorsTheBoundedCancellableDelay()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        var checkpoints = new List<LocalRepositoryCatalogHostedServiceCheckpoint>();
        var firstDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var temp = new MonitorTempDirectory { TimeProvider = time };
        await using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost(checkpoint =>
            {
                checkpoints.Add(checkpoint);
                if (checkpoint == LocalRepositoryCatalogHostedServiceCheckpoint.BeforeDelay)
                {
                    if (checkpoints.Count(item => item == checkpoint) == 1) firstDelay.TrySetResult();
                    else secondDelay.TrySetResult();
                }
            }));

        await app.StartAsync();
        await firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, checkpoints.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        await secondDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync();
        Assert.Equal(6, checkpoints.Count);
    }

    [Fact]
    public void RawDefault_PreservesTheRouteApplicationFactorySeam()
    {
        var factoryCalls = 0;
        LocalRepositoryCatalogApplication? factoryApplication = null;
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost(localRepositoryApplicationFactory: (databasePath, timeProvider) =>
            {
                factoryCalls++;
                var queue = new SqliteLocalRepositoryReconciliationStore(databasePath, timeProvider);
                factoryApplication = new LocalRepositoryCatalogApplication(
                    new SqliteLocalRepositoryCatalogStore(
                        databasePath,
                        queue,
                        new LocalRepositoryAssignmentResolver(),
                        timeProvider));
                return factoryApplication;
            }));

        Assert.Equal(1, factoryCalls);
        Assert.Same(factoryApplication, app.Services.GetRequiredService<LocalRepositoryCatalogApplication>());
    }

    [Fact]
    public void RawDefault_RegistersTheCatalogAfterProjectionAndSharesTheExactComposedInstances()
    {
        LocalRepositoryCatalogCompositionSnapshot? composition = null;
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost(
                startProjectionWorker: true,
                compositionObserver: observed => composition = observed));

        var observedComposition = Assert.IsType<LocalRepositoryCatalogCompositionSnapshot>(composition);
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        var projectionIndex = Array.FindIndex(hosted, service => service is ProjectionWorker);
        var catalogIndex = Array.FindIndex(hosted, service => service is LocalRepositoryCatalogHostedService);

        Assert.True(projectionIndex >= 0);
        Assert.True(catalogIndex > projectionIndex);
        Assert.Same(observedComposition.RawAvailability, app.Services.GetRequiredService<LocalRepositoryRawAvailabilityReader>());
        Assert.Same(observedComposition.Queue, app.Services.GetRequiredService<SqliteLocalRepositoryReconciliationStore>());
        Assert.Same(observedComposition.Resolver, app.Services.GetRequiredService<LocalRepositoryAssignmentResolver>());
        Assert.Same(observedComposition.Store, app.Services.GetRequiredService<SqliteLocalRepositoryCatalogStore>());
        Assert.Same(observedComposition.Worker, app.Services.GetRequiredService<LocalRepositoryReconciliationWorker>());
        Assert.Same(observedComposition.Application, app.Services.GetRequiredService<LocalRepositoryCatalogApplication>());
        Assert.Same(observedComposition.HostedService, hosted[catalogIndex]);
    }

    [Fact]
    public async Task RawDefault_SinglePassRecoversAnExpiredLeaseThroughTheWorker()
    {
        var time = new CountingMutableTimeProvider(DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        string? stateBeforeRunOnce = null;
        var workerBeforeRawAvailabilityClockReads = -1;
        using var temp = new MonitorTempDirectory { TimeProvider = time };
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost(checkpoint =>
            {
                if (checkpoint != LocalRepositoryCatalogHostedServiceCheckpoint.BeforeRunOnce) return;
                using var connection = Open(temp.DatabasePath);
                stateBeforeRunOnce = ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue;");
                time.ResetUtcNowCallCount();
            }, workerCheckpoint: new DelegatingReconciliationCheckpoint(reconciliationCheckpoint =>
            {
                if (reconciliationCheckpoint == LocalRepositoryReconciliationCheckpoint.BeforeRawAvailabilityRead)
                    workerBeforeRawAvailabilityClockReads = time.UtcNowCallCount;
            }), timeProvider: time));
        var rawStore = new RawTelemetryStore(
            temp.DatabasePath,
            app.Services.GetRequiredService<RetentionCatalogContext>(),
            time,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var rawRecordId = rawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "44444444444444444444444444444444",
            time.GetUtcNow(),
            null,
            "{}"));
        using (var connection = Open(temp.DatabasePath))
            InsertProjectedSpan(connection, rawRecordId, "44444444444444444444444444444444");

        var queue = app.Services.GetRequiredService<SqliteLocalRepositoryReconciliationStore>();
        var availability = app.Services.GetRequiredService<LocalRepositoryRawAvailabilityReader>();
        await queue.DiscoverAsync(availability, CancellationToken.None);
        Assert.NotNull(queue.TryClaimNext(time.GetUtcNow()).Lease);
        time.Advance(TimeSpan.FromSeconds(31));

        var hosted = Assert.Single(app.Services.GetServices<IHostedService>().OfType<LocalRepositoryCatalogHostedService>());
        await hosted.RunOnePassAsync(CancellationToken.None);

        using var verify = Open(temp.DatabasePath);
        Assert.Equal("leased", stateBeforeRunOnce);
        Assert.Equal(1, workerBeforeRawAvailabilityClockReads);
        Assert.Equal(2, ScalarLong(verify, "SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='failed_terminal';"));
        Assert.Equal(0, ScalarLong(verify, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE lease_token IS NOT NULL;"));
    }

    [Fact]
    public void ScopeFactory_IsLazyInRawDefault_AndAbsentInSanitizedOnly()
    {
        using var rawDefaultTemp = new MonitorTempDirectory();
        using var sanitizedOnlyTemp = new MonitorTempDirectory();
        using var rawDefault = MonitorHost.Build(
            new MonitorOptions(rawDefaultTemp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost());
        using var sanitizedOnly = MonitorHost.Build(
            new MonitorOptions(sanitizedOnlyTemp.DatabasePath, "http://127.0.0.1:0", true, MonitorOptions.DefaultMaxRequestBodyBytes),
            QuietHost());

        Assert.Single(rawDefault.Services.GetServices<IHostedService>()
            .OfType<LocalRepositoryCatalogHostedService>());
        Assert.Single(rawDefault.Services.GetServices<LocalRepositoryRawAvailabilityReader>());
        Assert.Single(rawDefault.Services.GetServices<SqliteLocalRepositoryReconciliationStore>());
        Assert.Single(rawDefault.Services.GetServices<LocalRepositoryAssignmentResolver>());
        Assert.Single(rawDefault.Services.GetServices<SqliteLocalRepositoryCatalogStore>());
        Assert.Single(rawDefault.Services.GetServices<LocalRepositoryReconciliationWorker>());
        Assert.Single(rawDefault.Services.GetServices<LocalRepositoryCatalogApplication>());
        var targetExistenceAuthority = Assert.Single(rawDefault.Services.GetServices<ILocalRepositoryTargetExistenceAuthority>());
        Assert.Same(SqliteLocalRepositoryTargetExistenceAuthority.Instance, targetExistenceAuthority);
        Assert.Same(targetExistenceAuthority, rawDefault.Services.GetRequiredService<ILocalRepositoryTargetExistenceAuthority>());
        Assert.Empty(rawDefault.Services.GetServices<ILocalRepositorySessionSnapshotContributor>());
        Assert.Empty(rawDefault.Services.GetServices<ILocalArchiveFactSnapshotContributor>());
        Assert.Throws<InvalidOperationException>(() =>
            rawDefault.Services.GetRequiredService<ILocalRepositoryScopeSnapshotService>());
        Assert.Empty(sanitizedOnly.Services.GetServices<IHostedService>()
            .OfType<LocalRepositoryCatalogHostedService>());
        Assert.Empty(sanitizedOnly.Services.GetServices<LocalRepositoryRawAvailabilityReader>());
        Assert.Empty(sanitizedOnly.Services.GetServices<SqliteLocalRepositoryReconciliationStore>());
        Assert.Empty(sanitizedOnly.Services.GetServices<LocalRepositoryAssignmentResolver>());
        Assert.Empty(sanitizedOnly.Services.GetServices<SqliteLocalRepositoryCatalogStore>());
        Assert.Empty(sanitizedOnly.Services.GetServices<LocalRepositoryReconciliationWorker>());
        Assert.Empty(sanitizedOnly.Services.GetServices<LocalRepositoryCatalogApplication>());
        Assert.Empty(sanitizedOnly.Services.GetServices<ILocalRepositoryTargetExistenceAuthority>());
        Assert.Null(sanitizedOnly.Services.GetService<ILocalRepositoryScopeSnapshotService>());
    }

    private static MonitorHostTestOptions QuietHost(
        Action<LocalRepositoryCatalogHostedServiceCheckpoint>? checkpoint = null,
        Func<string, TimeProvider, LocalRepositoryCatalogApplication>? localRepositoryApplicationFactory = null,
        bool startProjectionWorker = false,
        Action<LocalRepositoryCatalogCompositionSnapshot>? compositionObserver = null,
        ILocalRepositoryReconciliationCheckpoint? workerCheckpoint = null,
        TimeProvider? timeProvider = null) => new()
    {
        StartWriter = false,
        StartProjectionWorker = startProjectionWorker,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        LocalRepositoryCatalogHostedServiceCheckpoint = checkpoint,
        LocalRepositoryApplicationFactory = localRepositoryApplicationFactory,
        LocalRepositoryCatalogCompositionObserver = compositionObserver,
        LocalRepositoryReconciliationCheckpoint = workerCheckpoint,
        TimeProvider = timeProvider,
    };

    private sealed class CountingMutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public int UtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            UtcNowCallCount++;
            return current;
        }

        public void Advance(TimeSpan duration) => current = current.Add(duration);

        public void ResetUtcNowCallCount() => UtcNowCallCount = 0;
    }

    private sealed class DelegatingReconciliationCheckpoint(
        Action<LocalRepositoryReconciliationCheckpoint> reached) : ILocalRepositoryReconciliationCheckpoint
    {
        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint) => reached(checkpoint);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void InsertProjectedSpan(SqliteConnection connection, long rawRecordId, string traceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,conversation_id,duration_ms,start_time,end_time,projected_at)
            VALUES({rawRecordId},'{traceId}',NULL,NULL,0,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'2026-08-02T00:00:00.0000000+00:00');
            """;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

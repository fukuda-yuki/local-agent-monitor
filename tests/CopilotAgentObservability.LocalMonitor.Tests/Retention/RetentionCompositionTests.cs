using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCompositionTests
{
    [Fact]
    public void Build_RegistersOneConcreteRetentionRegistryAndWorkerByDefault()
    {
        using var tempDirectory = new MonitorTempDirectory();
        using var app = MonitorHost.Build(new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280));

        var registry = app.Services.GetRequiredService<RetentionAdapterRegistry>();
        Assert.IsType<SessionEventContentRetentionAdapter>(registry.Get(RetentionStoreKind.SessionEventContent));
        Assert.IsType<RawRecordRetentionAdapter>(registry.Get(RetentionStoreKind.RawRecord));
        Assert.IsType<MonitorAnalysisRetentionAdapter>(registry.Get(RetentionStoreKind.AnalysisRunRaw));
        Assert.IsType<SensitiveBundleRetentionAdapter>(registry.Get(RetentionStoreKind.SensitiveBundle));
        Assert.IsType<AnalysisSdkDirectoryRetentionAdapter>(registry.Get(RetentionStoreKind.AnalysisSdkDirectory));
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<RetentionCleanupWorker>());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDirectory.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_adapter_coverage WHERE coverage_version=1;";
        Assert.Equal(5L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Build_CanDisableRetentionWorkerForDeterministicHostTests()
    {
        using var tempDirectory = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartRetentionCleanupWorker = false, UseUserSecrets = false });

        Assert.Empty(app.Services.GetServices<IHostedService>().OfType<RetentionCleanupWorker>());
    }

    [Fact]
    public async Task HostLifecycle_StartsAndStopsTheSingleRetentionWorker()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var app = MonitorHost.Build(
            new MonitorOptions(tempDirectory.DatabasePath, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartRetentionCleanupWorker = true, UseUserSecrets = false });

        await app.StartAsync();
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<RetentionCleanupWorker>());
        await app.StopAsync();
    }
}

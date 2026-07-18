using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisStoreTests
{
    [Fact]
    public void CopilotAnalysisSettings_ReadsByokProviderConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Model"] = "glm-5.2",
                ["CopilotAnalysis:BaseDirectory"] = @"C:\tmp\cao-copilot-sdk",
                ["CopilotAnalysis:Provider:Type"] = "openai",
                ["CopilotAnalysis:Provider:BaseUrl"] = "https://example.test/v1/",
                ["CopilotAnalysis:Provider:WireApi"] = "completions",
                ["CopilotAnalysis:Provider:ApiKey"] = "secret-value",
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.True(settings.Enabled);
        Assert.Equal("glm-5.2", settings.Model);
        Assert.Equal(@"C:\tmp\cao-copilot-sdk", settings.BaseDirectory);
        Assert.NotNull(settings.Provider);
        Assert.Equal("openai", settings.Provider.Type);
        Assert.Equal("https://example.test/v1", settings.Provider.BaseUrl);
        Assert.Equal("completions", settings.Provider.WireApi);
    }

    [Fact]
    public void CopilotAnalysisSettings_ReadsDisabledFlag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Enabled"] = "false",
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal("gpt-5", settings.Model);
        Assert.Null(settings.Provider);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("600", 600)]
    [InlineData("0", 60)]
    [InlineData("-5", 60)]
    [InlineData("not-a-number", 60)]
    public void CopilotAnalysisSettings_ReadsTimeoutSeconds(string? configured, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:TimeoutSeconds"] = configured,
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.Equal(expected, settings.TimeoutSeconds);
    }

    [Fact]
    public void CopilotAnalysisSettings_RejectsUnsupportedWireApi()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Provider:Type"] = "openai",
                ["CopilotAnalysis:Provider:BaseUrl"] = "https://example.test/v1",
                ["CopilotAnalysis:Provider:WireApi"] = "invalid",
                ["CopilotAnalysis:Provider:ApiKey"] = "secret-value",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => CopilotAnalysisSettings.From(configuration));

        Assert.Contains("WireApi", exception.Message);
        Assert.DoesNotContain("secret-value", exception.Message);
    }

    [Fact]
    public void StartRun_PersistsQueuedRun()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        store.CreateSchema();

        var result = store.StartRun(
            traceId: "trace-analysis",
            rawRecordId: 42,
            spanId: "span-1",
            focus: MonitorAnalysisFocus.ToolUsage,
            requestedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var run = store.GetRun(result.RunId);

        Assert.NotNull(run);
        Assert.Equal("trace-analysis", run.TraceId);
        Assert.Equal(42, run.RawRecordId);
        Assert.Equal("span-1", run.SpanId);
        Assert.Equal(MonitorAnalysisFocus.ToolUsage, run.Focus);
        Assert.Equal(MonitorAnalysisStatus.Queued, run.Status);
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public void AppendEvent_AtomicallyCreatesAnalysisRawRetentionItem()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var result = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, requestedAt);

        store.AppendEvent(result.RunId, "progress", "raw local event", requestedAt.AddMinutes(1));

        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public async Task AppendEvent_ConcurrentFirstRawWritesCommitBothEventsAndOneCatalogItem()
    {
        using var temp = new MonitorTempDirectory();
        var setup = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        setup.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var run = setup.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, requestedAt);

        using var start = new Barrier(3);
        var first = Task.Run(() =>
        {
            start.SignalAndWait();
            new SqliteMonitorAnalysisStore(temp.DatabasePath).AppendEvent(run.RunId, "progress", "first raw event", requestedAt.AddMinutes(1));
        });
        var second = Task.Run(() =>
        {
            start.SignalAndWait();
            new SqliteMonitorAnalysisStore(temp.DatabasePath).AppendEvent(run.RunId, "progress", "second raw event", requestedAt.AddMinutes(2));
        });

        start.SignalAndWait();
        await Task.WhenAll(first, second);

        Assert.Equal(2, CountEvents(temp.DatabasePath, run.RunId));
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, run.RunId));
    }

    [Fact]
    public void AppendEvent_SourceWriteFailureRollsBackEventAndCatalog()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, phase =>
        {
            if (phase == MonitorAnalysisStoreWritePhase.AfterSourceWrite) throw new InvalidOperationException("injected");
        });
        store.CreateSchema();
        var result = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => store.AppendEvent(result.RunId, "progress", "raw local event", DateTimeOffset.UtcNow));

        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, result.RunId));
        Assert.Equal(0, CountEvents(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public void CompleteRun_PersistsLocalRawResultAndRepositorySafeSummary()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var result = store.StartRun(
            traceId: "trace-safe",
            rawRecordId: 7,
            spanId: null,
            focus: MonitorAnalysisFocus.Errors,
            requestedAt: requestedAt);

        store.AppendEvent(result.RunId, "progress", "Copilot read raw prompt SECRET_PROMPT_TEXT_MARKER", requestedAt.AddMinutes(1));
        store.CompleteRun(
            result.RunId,
            "Copilot raw result mentions SECRET_PROMPT_TEXT_MARKER and leak-marker@example.com",
            requestedAt.AddMinutes(2));

        var run = store.GetRun(result.RunId);
        Assert.NotNull(run);
        var summary = store.GenerateRepositorySafeSummary(result.RunId, requestedAt.AddMinutes(3));

        Assert.Equal(MonitorAnalysisStatus.Succeeded, run.Status);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", run.ResultMarkdown);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", summary.Markdown);
        Assert.DoesNotContain("leak-marker@example.com", summary.Markdown);
        Assert.Contains("trace-safe", summary.Markdown);
        Assert.Contains("raw record 7", summary.Markdown);
        Assert.Contains("errors", summary.Markdown);
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, result.RunId));
    }

    private static int CountRetentionItems(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_run_raw' AND source_item_id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountEvents(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_analysis_events WHERE run_id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

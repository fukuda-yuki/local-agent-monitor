using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
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
    }

    [Fact]
    public void CompleteRun_PersistsLocalRawResultAndRepositorySafeSummary()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        store.CreateSchema();
        var result = store.StartRun(
            traceId: "trace-safe",
            rawRecordId: 7,
            spanId: null,
            focus: MonitorAnalysisFocus.Errors,
            requestedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        store.AppendEvent(result.RunId, "progress", "Copilot read raw prompt SECRET_PROMPT_TEXT_MARKER", DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.CompleteRun(
            result.RunId,
            "Copilot raw result mentions SECRET_PROMPT_TEXT_MARKER and leak-marker@example.com",
            DateTimeOffset.UnixEpoch.AddMinutes(3));

        var run = store.GetRun(result.RunId);
        Assert.NotNull(run);
        var summary = store.GenerateRepositorySafeSummary(result.RunId, DateTimeOffset.UnixEpoch.AddMinutes(4));

        Assert.Equal(MonitorAnalysisStatus.Succeeded, run.Status);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", run.ResultMarkdown);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", summary.Markdown);
        Assert.DoesNotContain("leak-marker@example.com", summary.Markdown);
        Assert.Contains("trace-safe", summary.Markdown);
        Assert.Contains("raw record 7", summary.Markdown);
        Assert.Contains("errors", summary.Markdown);
    }
}

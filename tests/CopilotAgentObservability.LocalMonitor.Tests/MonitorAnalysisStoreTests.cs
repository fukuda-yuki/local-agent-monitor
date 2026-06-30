using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisStoreTests
{
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

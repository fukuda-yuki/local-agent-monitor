using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisPageTests
{
    [Fact]
    public async Task StandaloneHumanPageAndAsset_AreRetiredWithEmptyNotFound()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());

        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/historical-analysis"),
            new HttpRequestMessage(HttpMethod.Get, "/historical-analysis/"),
            new HttpRequestMessage(HttpMethod.Post, "/historical-analysis"),
            new HttpRequestMessage(HttpMethod.Get, "/monitor-historical-analysis.js"),
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal(0, response.Content.Headers.ContentLength);
                Assert.Null(response.Content.Headers.ContentType);
            }
        }
    }

    private static MonitorHostTestOptions QuietHostOptions() => new()
    {
        StartProjectionWorker = false,
        StartWriter = false,
        StartRetentionCleanupWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        UseUserSecrets = false,
    };
}

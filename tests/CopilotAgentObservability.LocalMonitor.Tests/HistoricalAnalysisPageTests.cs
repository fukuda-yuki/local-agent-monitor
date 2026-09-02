using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisPageTests
{
    public static TheoryData<string, string> RetiredSpellingsAndMethods
    {
        get
        {
            var values = new TheoryData<string, string>();
            foreach (var path in new[]
                     {
                         "/historical-analysis", "/historical-analysis/",
                         "/HISTORICAL-ANALYSIS", "/HiStOrIcAl-AnAlYsIs/",
                         "/historical-analysis?unknown=value", "/HiStOrIcAl-AnAlYsIs/?x=1",
                         "/monitor-historical-analysis.js", "/MoNiToR-HiStOrIcAl-AnAlYsIs.Js?x=1",
                     })
            {
                foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "RETIRE" })
                    values.Add(path, method);
            }
            return values;
        }
    }

    [Theory]
    [MemberData(nameof(RetiredSpellingsAndMethods))]
    public async Task StandaloneHumanPageAndAssetReturnTheSameEmptyNoStore404ForEveryMethod(
        string path,
        string method)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        foreach (var forbidden in new[] { "Allow", "Location", "ETag", "Last-Modified", "Set-Cookie" })
        {
            Assert.False(response.Headers.NonValidated.Contains(forbidden));
            Assert.False(response.Content.Headers.NonValidated.Contains(forbidden));
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

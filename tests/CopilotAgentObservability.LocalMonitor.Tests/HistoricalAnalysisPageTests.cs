using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisPageTests
{
    [Fact]
    public async Task RawDefaultPage_IsNoStoreAndPinsTheHostPosture()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());

        using var response = await host.Client.GetAsync("/historical-analysis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("raw-default · repository-safe response", body, StringComparison.Ordinal);
        var idIndex = body.IndexOf("id=\"historical-analysis-sanitized-only\"", StringComparison.Ordinal);
        var inputIndex = body.LastIndexOf("<input", idIndex, StringComparison.Ordinal);
        var checkbox = body[inputIndex..];
        checkbox = checkbox[..checkbox.IndexOf('>')];
        Assert.DoesNotContain("checked", checkbox, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", checkbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_RejectsCrossSiteReadsWithFixedNoLeakError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/historical-analysis");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"historical-analysis-error.v1","error":"cross_origin_forbidden"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_RejectsInvalidHostWithHistoricalAnalysisErrorContract()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/historical-analysis");
        request.Headers.Host = "remote.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"historical-analysis-error.v1","error":"invalid_host"}""",
            await response.Content.ReadAsStringAsync());
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

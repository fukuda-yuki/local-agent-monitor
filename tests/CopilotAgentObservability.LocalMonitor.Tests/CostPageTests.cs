using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostPageTests
{
    private const string SessionId = "0198f5b8-0c00-7000-8000-000000000001";
    private const string HistoricalSessionId = "11111111-2222-4333-8444-555555555555";
    private const string EstimateId = "pricing-estimate-" + SixtyFourA;
    private const string SixtyFourA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("/costs")]
    [InlineData("/costs?session_id=" + SessionId)]
    [InlineData("/costs?session_id=" + HistoricalSessionId)]
    [InlineData("/costs?session_id=" + SessionId + "&estimate_id=" + EstimateId)]
    public async Task Page_IsNoStoreAndAcceptsOnlyCanonicalContext(string target)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());

        using var response = await host.Client.GetAsync(target);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"cost-root\"", body, StringComparison.Ordinal);
        Assert.Contains("src=\"/costs.js\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/costs?estimate_id=" + EstimateId)]
    [InlineData("/costs?session_id=" + SessionId + "&session_id=" + SessionId)]
    [InlineData("/costs?unknown=value")]
    [InlineData("/costs?session_id=not-a-session")]
    [InlineData("/costs?session_id=11111111-2222-4333-8444-55555555555")]
    [InlineData("/costs?session_id=11111111-2222-4333-8444-55555555555A")]
    [InlineData("/costs?estimate_id=" + EstimateId + "&session_id=" + SessionId)]
    public async Task Page_RejectsInvalidOrNoncanonicalContext(string target)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());

        using var response = await host.Client.GetAsync(target);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"cost.error.v1","error":"cost_invalid_query"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_RejectsCrossSiteReadsWithFixedNoLeakError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/costs");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"cost.error.v1","error":"cross_origin_forbidden"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_RejectsInvalidHostWithCostErrorContract()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/costs");
        request.Headers.Host = "remote.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"cost.error.v1","error":"invalid_host"}""",
            await response.Content.ReadAsStringAsync());
    }

    private static MonitorHostTestOptions QuietHostOptions() => new()
    {
        StartProjectionWorker = false,
        StartWriter = false,
        StartRetentionCleanupWorker = false,
    };
}

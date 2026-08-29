using System.Net;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonRouteTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000001";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000002";

    [Fact]
    public async Task PreviewPublishesExactBufferedBytesAndSecurityHeaders()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema_version\":\"local-monitor-comparison-preview.response.v1\"}");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new StubApplication(bytes)));
        using var request = Post(host, $"/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/preview", PreviewBody());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ReadHeadHasGetStatusHeadersAndLengthWithNoBody()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema_version\":\"local-monitor-comparison-read.response.v1\"}");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new StubApplication(bytes)));
        using var request = new HttpRequestMessage(HttpMethod.Head, $"/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PreviewRejectsMissingCsrfBeforeApplicationWithoutEcho()
    {
        var application = new StubApplication("{}"u8.ToArray());
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(application));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/preview")
        {
            Content = new StringContent(PreviewBody(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"csrf_rejected\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, application.CallCount);
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories/018f0000-0000-7000-8000-000000000001/comparisons/018f0000-0000-7000-8000-000000000002?x=1")]
    [InlineData("/api/local-monitor/v1/repositories/018f0000-0000-7000-8000-000000000001/comparisons/018f0000-0000-7000-8000-000000000002/rows?q=x&family=tool")]
    [InlineData("/api/local-monitor/v1/repositories/018f0000-0000-7000-8000-000000000001/comparisons/018f0000-0000-7000-8000-000000000002/evidence?result_ordinal=0")]
    public async Task ReadRoutesRejectNonClosedQueryBeforeApplication(string path)
    {
        var application = new StubApplication("{}"u8.ToArray());
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(application));

        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, application.CallCount);
    }

    private static string PreviewBody() => """{"schema_version":"local-monitor-comparison-preview.request.v1","cohorts":{"a":["018f0000-0000-7000-8000-000000000003"],"b":["018f0000-0000-7000-8000-000000000004"]},"include_archived":false}""";

    private static HttpRequestMessage Post(RunningMonitorHost host, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static MonitorHostTestOptions Options(ILocalMonitorV1ComparisonApplication application) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false,
        UseUserSecrets = false,
        LocalMonitorV1ComparisonApplication = application,
    };

    private sealed class StubApplication(byte[] bytes) : ILocalMonitorV1ComparisonApplication
    {
        public int CallCount { get; private set; }
        public ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(LocalMonitorV1ComparisonOperation operation, string repositoryId, string? comparisonId, ReadOnlyMemory<byte> requestBody, string query, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new LocalMonitorV1ComparisonResponse(200, bytes));
        }
    }
}

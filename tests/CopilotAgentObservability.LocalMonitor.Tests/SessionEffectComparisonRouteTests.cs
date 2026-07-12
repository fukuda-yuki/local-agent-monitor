using System.Net;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionEffectComparisonRouteTests
{
    [Theory]
    [InlineData("{\"proposal_id\":\"00000000-0000-4000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"apply_id\":\"00000000-0000-7000-8000-000000000001\",\"sessions\":[]}")]
    public void Parser_rejects_non_v7_and_duplicate_request_fields(string body)
    {
        Assert.False(Sessions.EffectComparisonRequestParser.TryParse(System.Text.Encoding.UTF8.GetBytes(body), out _));
    }

    [Fact]
    public async Task Candidate_query_rejects_invalid_identifiers_with_fixed_no_store_error()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync("/api/session-workspace/effect-comparisons/candidates?proposal_id=not-a-uuid&apply_id=also-not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_rejects_cross_origin_csrf_media_and_oversize_before_persistence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var cases = new[]
        {
            Request("{}", origin: "http://example.test", csrf: true, media: true),
            Request("{}", origin: null, csrf: false, media: true),
            Request("{}", origin: null, csrf: true, media: false),
            Request(new string('x', 1_048_577), origin: null, csrf: true, media: true),
        };
        var expected = new[] { "cross_origin_forbidden", "csrf_required", "unsupported_media_type", "request_too_large" };
        foreach (var (request, error) in cases.Zip(expected))
        {
            using (request)
            {
                using var response = await host.Client.SendAsync(request);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                Assert.Equal($"{{\"error\":\"{error}\"}}", await response.Content.ReadAsStringAsync());
            }
        }
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Theory]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[],\"unknown\":true}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":0,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    public async Task Post_rejects_invalid_json_without_echo(string payload)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(Request(payload, null, true, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Request(string body, string? origin, bool csrf, bool media)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/effect-comparisons") { Content = new StringContent(body, Encoding.UTF8, media ? "application/json" : "text/plain") };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}

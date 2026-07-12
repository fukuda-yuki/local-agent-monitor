using System.Net;

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
}

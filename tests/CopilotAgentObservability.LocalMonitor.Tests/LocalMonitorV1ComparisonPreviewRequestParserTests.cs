using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonPreviewRequestParserTests
{
    [Fact]
    public void RejectsAggregateOccurrenceCountAbove200()
    {
        var a = string.Join(',', Enumerable.Range(0, 101).Select(index => $"\"018f0000-0000-7000-8000-{index:x12}\""));
        var b = string.Join(',', Enumerable.Range(101, 100).Select(index => $"\"018f0000-0000-7000-8000-{index:x12}\""));
        var bytes = Encoding.UTF8.GetBytes($$"""{"schema_version":"local-monitor-comparison-preview.request.v1","cohorts":{"a":[{{a}}],"b":[{{b}}]},"include_archived":false}""");

        var error = Assert.Throws<LocalMonitorV1ComparisonRequestException>(() =>
            LocalMonitorV1ComparisonParser.ParsePreview(bytes));

        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public void ParsesCanonicalClosedPreviewRequestWithoutRepairingOccurrences()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schema_version":"local-monitor-comparison-preview.request.v1","cohorts":{"a":["018f0000-0000-7000-8000-000000000001","018f0000-0000-7000-8000-000000000001"],"b":["018f0000-0000-7000-8000-000000000002"]},"include_archived":true}""");

        var request = LocalMonitorV1ComparisonParser.ParsePreview(bytes);

        Assert.Equal(2, request.CohortA.Count);
        Assert.Equal(request.CohortA[0], request.CohortA[1]);
        Assert.True(request.IncludeArchived);
    }

    [Theory]
    [InlineData("{\"cohorts\":{},\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"include_archived\":false}")]
    [InlineData("{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{\"a\":[\"018F0000-0000-7000-8000-000000000001\"],\"b\":[\"018f0000-0000-7000-8000-000000000002\"]},\"include_archived\":false}")]
    [InlineData("{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{\"a\":[\"018f0000-0000-7000-8000-000000000001\"],\"b\":[\"018f0000-0000-7000-8000-000000000002\"],\"extra\":[]},\"include_archived\":false}")]
    public void RejectsNoncanonicalOrOpenJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<LocalMonitorV1ComparisonRequestException>(() =>
            LocalMonitorV1ComparisonParser.ParsePreview(bytes));
    }
}

public sealed class LocalMonitorV1ComparisonCreateRequestParserTests
{
    [Fact]
    public void ParsesCanonicalClosedCreateRequest()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schema_version":"local-monitor-comparison-create.request.v1","cohorts":{"a":["018f0000-0000-7000-8000-000000000001"],"b":["018f0000-0000-7000-8000-000000000002"]},"include_archived":false,"selection_sha256":"1111111111111111111111111111111111111111111111111111111111111111","preview_revision":"2222222222222222222222222222222222222222222222222222222222222222"}""");

        var request = LocalMonitorV1ComparisonParser.ParseCreate(bytes);

        Assert.Equal(64, request.SelectionSha256.Length);
        Assert.Equal(64, request.PreviewRevision.Length);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{\"a\":[\"018f0000-0000-7000-8000-000000000001\"],\"b\":[\"018f0000-0000-7000-8000-000000000002\"]},\"include_archived\":false,\"preview_revision\":\"2222222222222222222222222222222222222222222222222222222222222222\",\"selection_sha256\":\"1111111111111111111111111111111111111111111111111111111111111111\"}")]
    [InlineData("{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{\"a\":[\"018f0000-0000-7000-8000-000000000001\"],\"b\":[\"018f0000-0000-7000-8000-000000000002\"]},\"include_archived\":false,\"selection_sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"preview_revision\":\"2222222222222222222222222222222222222222222222222222222222222222\"}")]
    public void RejectsWrongPropertyOrderOrNoncanonicalDigest(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<LocalMonitorV1ComparisonRequestException>(() =>
            LocalMonitorV1ComparisonParser.ParseCreate(bytes));
    }
}

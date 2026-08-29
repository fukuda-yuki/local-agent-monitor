namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonSerializerTests
{
    [Fact]
    public void PreviewSerializerMatchesFrozenGoldenBytes()
    {
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002", excluded = "018f0000-0000-7000-8000-000000000003";
        var preview = new LocalComparisonProjectionPreview(true, new string('1', 64), new string('2', 64),
            [new("a", 1, a), new("a", 2, excluded), new("b", 1, b)],
            [Candidate(a, new string('3', 64), 1), Candidate(b, new string('4', 64), 2)],
            [new("a", 2, excluded, "session_archived")]);

        var actual = ComparisonJson.Preview(preview);
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison", "local-monitor-comparison-preview.response.json"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PreviewSerializerWritesNullForUnavailableSourceAndModelFacts()
    {
        const string a = "018f0000-0000-7000-8000-000000000001";
        const string b = "018f0000-0000-7000-8000-000000000002";
        var unavailable = Candidate(a, new string('3', 64), 1) with
        {
            Sources = [],
            Models = [],
            SourcesState = "source_unsupported",
            ModelsState = "not_observed",
        };
        var preview = new LocalComparisonProjectionPreview(true, new string('1', 64), new string('2', 64),
            [new("a", 1, a), new("b", 1, b)],
            [unavailable, Candidate(b, new string('4', 64), 2)],
            []);

        using var json = System.Text.Json.JsonDocument.Parse(ComparisonJson.Preview(preview));
        var metadata = json.RootElement.GetProperty("included")[0].GetProperty("metadata");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, metadata.GetProperty("source").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, metadata.GetProperty("model").ValueKind);
        Assert.DoesNotContain("unknown", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static LocalComparisonProjectionCandidate Candidate(string id, string revision, long sessionRevision) => new(id, "018f0000-0000-7000-8000-000000000100", LocalComparisonCandidateState.Included, false, "active", ["synthetic"], "recorded", ["test-model"], "recorded", 5, "full", ["tokens"], sessionRevision, revision);
}

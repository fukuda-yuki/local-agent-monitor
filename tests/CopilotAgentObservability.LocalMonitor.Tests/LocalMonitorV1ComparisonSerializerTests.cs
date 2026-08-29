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

    private static LocalComparisonProjectionCandidate Candidate(string id, string revision, long sessionRevision) => new(id, "018f0000-0000-7000-8000-000000000100", LocalComparisonCandidateState.Included, false, "active", ["synthetic"], ["test-model"], 5, "full", ["tokens"], sessionRevision, revision);
}

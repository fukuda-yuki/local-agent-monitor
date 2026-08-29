namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonSerializerTests
{
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000010";
    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string ExecutionId = "018f0000-0000-7000-8000-000000000020";
    private const string NodeId = "node-11111111111111111111111111111111";

    [Fact]
    public void PreviewSerializerMatchesFrozenGoldenBytes()
    {
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002", excluded = "018f0000-0000-7000-8000-000000000003";
        var preview = new LocalComparisonProjectionPreview(true, new string('1', 64), new string('2', 64),
            [new("a", 1, a), new("a", 2, excluded), new("b", 1, b)],
            [Candidate(a, new string('3', 64), 1), Candidate(b, new string('4', 64), 2)],
            [new("a", 2, excluded, "session_archived", Candidate(excluded, new string('5', 64), 3) with
            {
                IsArchived = true, ArchiveState = "archived", ArchiveExclusionReason = "session_archived",
            })]);

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

    [Theory]
    [InlineData("workspace_session", SessionId, null, null, null, null, "/sessions/018f0000-0000-7000-8000-000000000001")]
    [InlineData("session_run", ExecutionId, null, null, null, null, "/sessions/018f0000-0000-7000-8000-000000000001?execution=018f0000-0000-7000-8000-000000000020")]
    [InlineData("workspace_node", NodeId, null, null, null, NodeId, "/sessions/018f0000-0000-7000-8000-000000000001?node=node-11111111111111111111111111111111")]
    [InlineData("workspace_node", NodeId, null, null, ExecutionId, NodeId, "/sessions/018f0000-0000-7000-8000-000000000001?execution=018f0000-0000-7000-8000-000000000020&node=node-11111111111111111111111111111111")]
    public void EvidenceSerializerUsesAuthoritativeCanonicalSessionLocation(
        string sourceKind,
        string sourceIdentity,
        string? traceId,
        string? spanId,
        string? eventId,
        string? expectedNode,
        string expectedLocation)
    {
        var stored = new LocalComparisonStoredEvidence(
            ComparisonId, 1, 0, "value", "a", SessionId, "recorded", "1",
            sourceKind, sourceIdentity, traceId, spanId, eventId, new string('6', 64));

        using var json = System.Text.Json.JsonDocument.Parse(
            ComparisonJson.Evidence(ComparisonId, 1, "value", [stored], null));
        var item = json.RootElement.GetProperty("items")[0];

        Assert.Equal(sourceKind == "session_run" ? ExecutionId : eventId, item.GetProperty("execution_id").GetString());
        Assert.Equal(expectedNode, item.GetProperty("node_id").GetString());
        Assert.Equal(expectedLocation, item.GetProperty("session_location").GetString());
    }

    [Fact]
    public void EvidenceFieldResolverFreezesAcceptedFieldsByResultFamily()
    {
        Assert.Equal(["count"], Fields(1, "scalar", "included_session_count"));
        Assert.Equal(["condition"], Fields(1, "condition", "archived_inclusion"));
        Assert.Empty(Fields(1, "scalar", "archived_inclusion"));
        Assert.Empty(Fields(1, "condition", "unsupported_target"));
        Assert.Equal(["count"], Fields(5, "skill", "skill.synthetic"));
        Assert.Equal(["count", "error_count", "retry_count"], Fields(6, "tool", "tool.synthetic"));
        Assert.Equal(["count", "total_tokens"], Fields(7, "subagent", "subagent.synthetic"));
        Assert.Equal(["condition"], Fields(9, "condition", "models"));
        Assert.Equal(
            ["value", "available_count", "median", "minimum", "maximum", "total", "absolute_difference", "relative_difference_percent", "total_tokens"],
            Fields(2, "scalar", "total_tokens"));
        Assert.Empty(Fields(1, "scalar", "unsupported_target"));

        static IReadOnlyList<string> Fields(int section, string kind, string key) =>
            LocalMonitorV1ComparisonEvidenceFieldResolver.AcceptedFields(
                LocalComparisonStoredResult.Create(ComparisonId, 1, section, kind, key,
                    [new KeyValuePair<string, string>("value", "1")]));
    }

    private static LocalComparisonProjectionCandidate Candidate(string id, string revision, long sessionRevision) => new(id, "018f0000-0000-7000-8000-000000000100", LocalComparisonCandidateState.Included, false, "active", 1, "active", 1, null, ["synthetic"], "recorded", ["test-model"], "recorded", 5, "full", ["tokens"], ["source-1"], ["adapter-1"], sessionRevision, revision);
}

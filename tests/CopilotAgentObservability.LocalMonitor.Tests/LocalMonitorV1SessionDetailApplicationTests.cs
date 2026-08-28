using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailApplicationTests
{
    [Theory]
    [InlineData("tool", "{\"kind\":\"tool\",\"caller\":{\"state\":\"recorded\",\"node_id\":\"node-caller\"},\"lifecycle\":{\"state\":\"recorded\",\"value\":\"completed\"},\"status\":{\"state\":\"recorded\",\"value\":\"completed\"},\"exit\":{\"state\":\"source_unsupported\"},\"mcp_server_identity\":{\"state\":\"recorded\",\"value\":\"server-hash\"},\"mcp_server_name\":{\"state\":\"source_unsupported\",\"value\":null},\"mcp_tool_name\":{\"state\":\"recorded\",\"value\":\"read_file\"},\"input\":{\"state\":\"available\",\"available\":true},\"result\":{\"state\":\"deleted\",\"available\":false},\"error\":{\"state\":\"not_captured\",\"available\":false},\"retry\":{\"state\":\"recorded\",\"node_ids\":[\"node-retry\"]},\"recovery\":{\"state\":\"recorded\",\"node_ids\":[\"node-recovery\"]},\"child_activity\":{\"skill\":{\"state\":\"not_observed\",\"count\":null},\"tool\":{\"state\":\"not_observed\",\"count\":null},\"subagent\":{\"state\":\"not_observed\",\"count\":null},\"error\":{\"state\":\"not_observed\",\"count\":null},\"retry\":{\"state\":\"not_observed\",\"count\":null}},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    [InlineData("skill", "{\"kind\":\"skill\",\"current_valid_state\":\"current\",\"source\":{\"state\":\"recorded\",\"value\":\"registry\"},\"trigger\":{\"state\":\"recorded\",\"value\":\"explicit\"},\"inventory_reference\":{\"state\":\"source_unsupported\",\"value\":null},\"historical_snapshot_reference\":{\"state\":\"recorded\",\"value\":\"snapshot-ref\"}}")]
    [InlineData("subagent", "{\"kind\":\"subagent\",\"lifecycle\":{\"selected\":{\"state\":\"not_observed\"},\"started\":{\"state\":\"recorded\"},\"completed\":{\"state\":\"recorded\"},\"failed\":{\"state\":\"not_observed\"},\"deselected\":{\"state\":\"source_unsupported\"}},\"input\":{\"state\":\"not_captured\",\"available\":false},\"activity\":{\"skill\":{\"state\":\"not_observed\",\"count\":null},\"tool\":{\"state\":\"not_observed\",\"count\":null},\"subagent\":{\"state\":\"not_observed\",\"count\":null},\"error\":{\"state\":\"not_observed\",\"count\":null},\"retry\":{\"state\":\"not_observed\",\"count\":null}},\"tokens\":{\"authority\":\"none\",\"state\":\"not_observed\",\"available_execution_count\":0,\"total_execution_count\":1,\"input\":{\"state\":\"not_observed\",\"value\":null},\"output\":{\"state\":\"not_observed\",\"value\":null},\"total\":{\"state\":\"not_observed\",\"value\":null},\"reasoning\":{\"state\":\"not_observed\",\"value\":null},\"cache_read\":{\"state\":\"not_observed\",\"value\":null},\"cache_creation\":{\"state\":\"not_observed\",\"value\":null},\"new_input\":{\"state\":\"not_observed\",\"value\":null},\"cache_read_ratio_basis_points\":{\"state\":\"not_observed\",\"value\":null}},\"children\":{\"state\":\"recorded\",\"count\":0},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    [InlineData("error", "{\"kind\":\"error\",\"error_code\":{\"state\":\"not_observed\",\"value\":null},\"message\":{\"state\":\"available\",\"available\":true},\"status\":{\"state\":\"recorded\",\"value\":\"failed\"},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    [InlineData("permission", "{\"kind\":\"permission\",\"decision\":{\"state\":\"recorded\",\"value\":\"allow\"},\"wait\":{\"state\":\"source_unsupported\"},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    [InlineData("event", "{\"kind\":\"event\",\"event_name\":{\"state\":\"recorded\",\"value\":\"UserPromptSubmit\"},\"source_time\":{\"state\":\"missing\",\"value\":null},\"content\":{\"state\":\"available\",\"available\":true},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    [InlineData("retry", "{\"kind\":\"retry\",\"attempt\":{\"state\":\"not_observed\",\"value\":null},\"target\":{\"state\":\"not_observed\",\"node_id\":null},\"recovered\":{\"state\":\"not_observed\",\"value\":null},\"source_references\":{\"state\":\"recorded\",\"references\":[{\"source_kind\":\"session_event\",\"source_identity\":\"event-source\",\"trace_id\":null,\"span_id\":null,\"event_id\":\"event-source\"}]}}")]
    public void NodeV2MetadataSerializesEveryKindFromClosedAuthenticatedFacts(string kind, string expected)
    {
        var activity = EmptyActivity(); var tokens = EmptyTokens(); const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string executionId = "018f0000-0000-7000-8000-000000000003"; const string rootId = "node-root"; const string nodeId = "node-target";
        var reference = new LocalWorkspaceNodeSourceReferenceDetail("session_event", "event-source", null, null, "event-source", AuthorityValidated: true);
        var root = new LocalWorkspaceNodeDetail(rootId, sessionId, executionId, "execution_root", "run", 0, null, "exact", "execution", "not_observed", null, "completed", "completed", "missing", null, null, null, activity, tokens, null, null, null, 1, SourceReferences: []);
        var node = new LocalWorkspaceNodeDetail(nodeId, sessionId, executionId, "session_event", "event-source", 1, rootId, "exact", kind,
            kind == "event" ? "recorded" : "not_observed", kind == "event" ? "UserPromptSubmit" : null,
            kind is "tool" or "subagent" ? "completed" : kind == "error" ? "failed" : "unknown",
            kind == "tool" ? "completed" : kind == "error" ? "failed" : "unknown", "missing", null, null, null, activity, tokens, null, null, "event-source", SourceReferences: [reference],
            ToolMetadata: kind == "tool" ? new("recorded", "node-caller", "recorded", "recorded", "not_observed", "source_unsupported", null, "recorded", "server-hash", "source_unsupported", null, "recorded", "read_file", "recorded", "recorded", "not_observed", null, [reference]) : null,
            SkillMetadata: kind == "skill" ? new("current", "recorded", "registry", "recorded", "explicit", "source_unsupported", null, "recorded", "snapshot-ref") : null,
            SubagentLifecycle: kind == "subagent" ? new("not_observed", "recorded", "recorded", "not_observed", "source_unsupported", "not_captured", [reference]) : null,
            PermissionMetadata: kind == "permission" ? new("recorded", "allow", "source_unsupported", null) : null);
        var execution = new LocalWorkspaceExecutionDetail(executionId, sessionId, "session_run", "run", 0, "completed", "completed", null, null, "missing", null, null, null, activity, tokens, ChildCount: 1);
        var content = kind switch { "tool" => new[] { new LocalWorkspaceContentAvailability(nodeId, "tool_input", "available"), new(nodeId, "tool_result", "deleted") }, "error" => [new(nodeId, "error_message", "available")], "event" => [new(nodeId, "event_content", "available")], _ => [] };
        var edges = kind == "tool" ? new[] { new LocalWorkspaceNodeEdgeDetail(nodeId, "node-retry", "retry", "exact", 0), new(nodeId, "node-recovery", "recovery", "exact", 1) } : [];
        var row = new LocalWorkspaceProjectionRow(sessionId, 0, 0, "not_observed", null, "completed", "full", new("not_observed", []), new("not_observed", []), activity, tokens, "missing", null, null, null, null, [], "seed");
        var snapshot = new LocalRepositorySessionDetailSnapshot(new(sessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned, LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null), new([execution], [root, node], edges, content, CanonicalRevisionInput: "canonical", SkillRegistryGenerationIdentity: "generation"), new string('1', 64));

        using var document = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeNode(snapshot, nodeId));
        Assert.Equal(expected, document.RootElement.GetProperty("node").GetProperty("metadata").GetRawText());
    }

    [Fact]
    public void SerializerContractPreservesRecordedSkillZeroAndOptionalRichTokenComponentsWithoutInventingProductionEvidence()
    {
        var recordedZero = new LocalWorkspaceFact<long>("recorded", 0);
        var tokens = new LocalWorkspaceTokenFacts("llm_span", "recorded", 1, 1,
            new("recorded", 10), new("recorded", 5), new("recorded", 15), new("recorded", 2),
            new("recorded", 4), new("recorded", 1), new("recorded", 6), new("recorded", 4000));
        var row = new LocalWorkspaceProjectionRow("018f0000-0000-7000-8000-000000000001", 0, 0, "recorded", "instruction",
            "completed", "full", new("recorded", ["vscode"]), new("recorded", ["gpt-5.6-sol"]),
            new(recordedZero, recordedZero, recordedZero, recordedZero, recordedZero), tokens, "recorded",
            "2026-08-26T01:02:03.0000000+00:00", "2026-08-26T01:02:04.0000000+00:00",
            "2026-08-26T01:02:04.0000000+00:00", 1000, [], "serializer-contract");
        var snapshot = new LocalRepositorySessionDetailSnapshot(
            new(row.SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
                LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null),
            new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64));

        var json = System.Text.Encoding.UTF8.GetString(LocalMonitorV1SessionDetailApplication.SerializeSummary(snapshot));

        Assert.Contains("\"skill\":{\"state\":\"recorded\",\"count\":0}", json, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":{\"state\":\"recorded\",\"value\":2}", json, StringComparison.Ordinal);
        Assert.Contains("\"new_input\":{\"state\":\"recorded\",\"value\":6}", json, StringComparison.Ordinal);
        Assert.Contains("\"cache_read_ratio_basis_points\":{\"state\":\"recorded\",\"value\":4000}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineCursor_RoundTripsTheExact119ByteFrame()
    {
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var filter = new LocalMonitorV1TimelineFilter(
            "018f0000-0000-7000-8000-000000000001",
            new string('1', 64),
            "018f0000-0000-7000-8000-000000000003",
            null,
            100);
        var position = new LocalMonitorV1TimelinePosition(
            0,
            638918245230000000,
            7,
            "node-00000000000000000000000000000002");

        var cursor = LocalMonitorV1TimelineCursor.Encode(key, filter, position);

        Assert.Equal(159, cursor.Length);
        Assert.True(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter, out var decoded));
        Assert.Equal(position, decoded);
        Assert.False(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter with { Limit = 101 }, out _));
    }

    private static LocalWorkspaceActivityFacts EmptyActivity()
    {
        var fact = new LocalWorkspaceFact<long>("not_observed", null);
        return new(fact, fact, fact, fact, fact);
    }

    private static LocalWorkspaceTokenFacts EmptyTokens()
    {
        var fact = new LocalWorkspaceFact<long>("not_observed", null);
        return new("none", "not_observed", 0, 1, fact, fact, fact, fact, fact, fact, fact, fact);
    }
}

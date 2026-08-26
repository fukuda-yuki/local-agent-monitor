using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionDetailAuthorityMatrixTests
{
    [Theory]
    [InlineData("retry")]
    [InlineData("recovery")]
    [InlineData("children")]
    public void NodeApplicationAcceptsExactlyTwoHundredOrderedRelationsAndRejectsTwoHundredOne(string relation)
    {
        var accepted = Snapshot(relation, 200);
        var bytes = LocalMonitorV1SessionDetailApplication.SerializeNode(accepted, TargetNodeId);
        Assert.NotEmpty(bytes);

        var rejected = Snapshot(relation, 201);
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeNode(rejected, TargetNodeId));
        Assert.Equal("workspace_too_large", error.Error);
    }

    [Theory]
    [InlineData("cache_exceeds_input")]
    [InlineData("new_input_mismatch")]
    public void CoordinatorAuthorityRejectsInvalidTokenArithmetic(string corruption)
    {
        var detail = Snapshot("retry", 0).Detail;
        var badTokens = corruption == "cache_exceeds_input"
            ? Tokens(input: 10, cacheRead: 11, newInput: null)
            : Tokens(input: 10, cacheRead: 4, newInput: 9);
        var execution = detail.Executions[0] with { Tokens = badTokens };
        var invalid = detail with { Executions = new[] { execution } };

        var error = Assert.Throws<LocalWorkspaceSessionDetailException>(() =>
            SqliteLocalRepositoryScopeSnapshotService.ValidateDetailForTest(SessionId,
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: TargetNodeId), invalid));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("duplicate_roots")]
    [InlineData("multiple_unknown_groups")]
    [InlineData("negative_ordinal")]
    [InlineData("wrong_execution_id")]
    [InlineData("wrong_node_id")]
    [InlineData("parent_edge_mismatch")]
    [InlineData("cross_execution_edge")]
    [InlineData("path_not_terminating")]
    [InlineData("invalid_activity")]
    [InlineData("duplicate_metadata")]
    [InlineData("unsorted_metadata")]
    public void CoordinatorAuthorityRejectsMalformedClosedFacts(string corruption)
    {
        var invalid = Corrupt(Snapshot("retry", 0).Detail, corruption);
        var error = Assert.Throws<LocalWorkspaceSessionDetailException>(() =>
            SqliteLocalRepositoryScopeSnapshotService.ValidateDetailForTest(SessionId,
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: TargetNodeId), invalid));
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot(string relation, int count)
    {
        var activity = Activity();
        var tokens = Tokens();
        var row = new LocalWorkspaceProjectionRow(SessionId, 0, 0, "not_observed", null, "completed", "full",
            new("recorded", ["copilot-sdk"]), new("not_observed", []), activity, tokens,
            "missing", null, null, null, null, [], "revision");
        var scope = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 0,
            LocalRepositoryScopeAssignmentState.Unassigned, LocalRepositoryScopeAssignmentAuthority.None,
            null, [], true, true, true, LocalArchiveState.Active, 0, true, null);
        var execution = new LocalWorkspaceExecutionDetail(ExecutionId, SessionId, "session_run", "run-1", 0,
            "completed", "completed", null, null, "missing", null, null, null, activity, tokens, "copilot-sdk", null);
        var root = Node(RootNodeId, "execution_root", "run-1", "execution", null, 0, tokens);
        var target = Node(TargetNodeId, "session_event", "target", "event", RootNodeId, 1, tokens);
        var nodes = new List<LocalWorkspaceNodeDetail> { root, target };
        var edges = new List<LocalWorkspaceNodeEdgeDetail>();
        for (var index = count - 1; index >= 0; index--)
        {
            var identity = $"{relation}-{index:D3}";
            var id = LocalWorkspaceProjectionStore.StableNodeId("session_event", identity);
            var parent = relation == "children" ? TargetNodeId : RootNodeId;
            nodes.Add(Node(id, "session_event", identity, "event", parent, index + 2, tokens));
            if (relation != "children") edges.Add(new(TargetNodeId, id, relation, "exact", index));
        }
        var detail = new LocalWorkspaceSessionDetailContribution([execution], nodes, edges, [], [], [], null, null, "canonical", "registry");
        return new(scope, detail, new string('1', 64));
    }

    private static LocalWorkspaceNodeDetail Node(string id, string sourceKind, string identity, string kind,
        string? parent, long ordinal, LocalWorkspaceTokenFacts tokens) =>
        new(id, SessionId, ExecutionId, sourceKind, identity, ordinal, parent, "exact", kind, "not_observed", null,
            "completed", "completed", "missing", null, null, null, Activity(), tokens, null, null, null);

    private static LocalWorkspaceActivityFacts Activity()
    {
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        return new(zero, zero, zero, zero, zero);
    }

    private static LocalWorkspaceSessionDetailContribution Corrupt(LocalWorkspaceSessionDetailContribution detail, string corruption)
    {
        var executions = detail.Executions.ToList();
        var nodes = detail.Nodes.ToList();
        var edges = detail.Edges.ToList();
        IReadOnlyList<string> nativeIds = detail.NativeSessionIds ?? throw new InvalidOperationException();
        IReadOnlyList<string> versions = detail.Versions ?? throw new InvalidOperationException();
        switch (corruption)
        {
            case "duplicate_roots":
                nodes.Add(Node(LocalWorkspaceProjectionStore.StableNodeId("execution_root", "other-root"), "execution_root", "other-root", "execution", null, 2, Tokens()));
                break;
            case "multiple_unknown_groups":
                nodes.Add(Node(LocalWorkspaceProjectionStore.StableNodeId("unknown_relation_group", "unknown-a"), "unknown_relation_group", "unknown-a", "unknown_relation_group", null, 2, Tokens()) with { RelationshipAuthority = "unknown" });
                nodes.Add(Node(LocalWorkspaceProjectionStore.StableNodeId("unknown_relation_group", "unknown-b"), "unknown_relation_group", "unknown-b", "unknown_relation_group", null, 3, Tokens()) with { RelationshipAuthority = "unknown" });
                break;
            case "negative_ordinal": nodes[1] = nodes[1] with { SourceOrdinal = -1 }; break;
            case "wrong_execution_id": executions[0] = executions[0] with { ExecutionId = "018f0000-0000-7000-8000-000000000099" }; break;
            case "wrong_node_id": nodes[1] = nodes[1] with { NodeId = "node-00000000000000000000000000000000" }; break;
            case "parent_edge_mismatch": edges.Add(new(TargetNodeId, RootNodeId, "parent", "exact", 0)); nodes[1] = nodes[1] with { ParentNodeId = null }; break;
            case "cross_execution_edge":
                var otherExecution = LocalWorkspaceProjectionStore.StableExecutionId(SessionId, "session_run", "run-2");
                executions.Add(executions[0] with { ExecutionId = otherExecution, SourceIdentity = "run-2", SourceOrdinal = 1 });
                var other = Node(LocalWorkspaceProjectionStore.StableNodeId("execution_root", "run-2"), "execution_root", "run-2", "execution", null, 0, Tokens()) with { ExecutionId = otherExecution };
                nodes.Add(other); edges.Add(new(TargetNodeId, other.NodeId, "retry", "exact", 0));
                break;
            case "path_not_terminating": nodes[1] = nodes[1] with { ParentNodeId = null }; break;
            case "invalid_activity": nodes[1] = nodes[1] with { Activity = new(new("recorded", -1), new("recorded", 0), new("recorded", 0), new("recorded", 0), new("recorded", 0)) }; break;
            case "duplicate_metadata": nativeIds = ["native", "native"]; break;
            case "unsorted_metadata": versions = ["z", "a"]; break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        return detail with { Executions = executions, Nodes = nodes, Edges = edges, NativeSessionIds = nativeIds, Versions = versions };
    }

    private static LocalWorkspaceTokenFacts Tokens(long? input = null, long? cacheRead = null, long? newInput = null)
    {
        LocalWorkspaceFact<long> Fact(long? value) => value is null ? new("not_observed", null) : new("recorded", value);
        return new(input is null ? "none" : "session_run", input is null ? "not_observed" : "recorded",
            input is null ? 0 : 1, 1, Fact(input), Fact(null), Fact(null), Fact(null), Fact(cacheRead), Fact(null), Fact(newInput), Fact(null));
    }

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private static readonly string ExecutionId = LocalWorkspaceProjectionStore.StableExecutionId(SessionId, "session_run", "run-1");
    private static readonly string RootNodeId = LocalWorkspaceProjectionStore.StableNodeId("execution_root", "run-1");
    private static readonly string TargetNodeId = LocalWorkspaceProjectionStore.StableNodeId("session_event", "target");
}

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

    [Fact]
    public void CoordinatorAuthorityAcceptsExactClosedV5KindFacts()
    {
        var detail = SemanticDetail();

        SqliteLocalRepositoryScopeSnapshotService.ValidateDetailForTest(SessionId,
            new(LocalRepositorySessionDetailRequestKind.Summary, SessionId), detail);
    }

    [Fact]
    public void CoordinatorSemanticMetadataCarriesTheSameExactReferencesAsItsNode()
    {
        Assert.NotNull(typeof(LocalWorkspaceToolMetadataDetail).GetProperty("SourceReferences"));
        Assert.NotNull(typeof(LocalWorkspaceSubagentLifecycleDetail).GetProperty("SourceReferences"));
    }

    [Theory]
    [InlineData("source_kind_kind")]
    [InlineData("uppercase_carrier")]
    [InlineData("missing_tool_metadata")]
    [InlineData("extra_tool_metadata")]
    [InlineData("invalid_tool_state_value")]
    [InlineData("missing_subagent_metadata")]
    [InlineData("invalid_subagent_lifecycle")]
    [InlineData("invalid_skill_state_value")]
    [InlineData("invalid_permission_state_value")]
    [InlineData("missing_semantic_references")]
    [InlineData("overflow_references")]
    [InlineData("duplicate_references")]
    [InlineData("unsorted_references")]
    [InlineData("empty_reference_identity")]
    [InlineData("malformed_trace_span")]
    [InlineData("root_reference_kind")]
    [InlineData("raw_reference_kind")]
    [InlineData("raw_reference_identity")]
    [InlineData("raw_reference_trace")]
    [InlineData("skill_reference_kind")]
    [InlineData("skill_reference_trace")]
    [InlineData("subagent_reference_kind")]
    [InlineData("mixed_tool_references")]
    [InlineData("multiple_otel_tool_references")]
    [InlineData("otel_tool_digest")]
    [InlineData("tool_metadata_references")]
    [InlineData("subagent_metadata_references")]
    [InlineData("same_kind_foreign_reference")]
    [InlineData("technical_reference_drift")]
    public void CoordinatorAuthorityRejectsMalformedSemanticClosedFacts(string corruption)
    {
        var detail = CorruptSemantic(SemanticDetail(), corruption);

        var error = Assert.Throws<LocalWorkspaceSessionDetailException>(() =>
            SqliteLocalRepositoryScopeSnapshotService.ValidateDetailForTest(SessionId,
                new(LocalRepositorySessionDetailRequestKind.Summary, SessionId), detail));

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
            "completed", "completed", "missing", null, null, null, Activity(), tokens, null, null,
            sourceKind == "session_event" ? identity : null,
            SourceReferences: sourceKind switch
            {
                "execution_root" => [Ref("session_run", identity, null, null, null)],
                "session_event" => [Ref("session_event", identity, null, null, identity)],
                "unknown_relation_group" => [],
                _ => null,
            });

    private static LocalWorkspaceSessionDetailContribution SemanticDetail()
    {
        var tokens = Tokens();
        var execution = new LocalWorkspaceExecutionDetail(ExecutionId, SessionId, "session_run", "run-1", 0,
            "completed", "completed", null, null, "missing", null, null, null, Activity(), tokens, "copilot-sdk", null);
        var root = Node(RootNodeId, "execution_root", "run-1", "execution", null, 0, tokens);
        var toolTrace = new string('c', 32);
        var toolSpan = new string('d', 16);
        var toolDigest = SemanticDigest("otel_tool", toolTrace, toolSpan);
        var subagentDigest = SemanticDigest("session_sdk_subagent", "native-session", "native-child");
        LocalWorkspaceNodeSourceReferenceDetail[] toolReferences = [Ref("otel_span", "tool-event", toolTrace, toolSpan, "tool-event")];
        var tool = Node(LocalWorkspaceProjectionStore.StableNodeId("semantic_tool", toolDigest), "semantic_tool", toolDigest,
            "tool", RootNodeId, 1, tokens) with
        {
            TraceId = toolTrace,
            SpanId = toolSpan,
            EventId = "tool-event",
            SourceReferences = toolReferences,
            ToolMetadata = ToolMetadata(toolReferences),
        };
        LocalWorkspaceNodeSourceReferenceDetail[] subagentReferences = [Ref("session_event", "subagent-event", null, null, "subagent-event")];
        var subagent = Node(LocalWorkspaceProjectionStore.StableNodeId("semantic_subagent", subagentDigest), "semantic_subagent", subagentDigest,
            "subagent", RootNodeId, 2, tokens) with
        {
            SourceReferences = subagentReferences,
            SubagentLifecycle = new("not_observed", "recorded", "recorded", "not_observed", "not_observed", "source_unsupported", subagentReferences),
        };
        var skill = Node(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", "skill-identity"), "skill_invocation", "skill-identity",
            "skill", RootNodeId, 3, tokens) with
        {
            TraceId = new string('e', 32),
            SpanId = new string('f', 16),
            SourceReferences =
            [
                Ref("skill_claim", "otel:skill-source", new string('e', 32), new string('f', 16), "otel-skill-source"),
                Ref("skill_claim", "sdk:skill-source", null, null, "sdk-skill-source"),
            ],
            SkillMetadata = new("current", "recorded", "project", "not_observed", null, "unavailable", null, "not_observed", null),
        };
        var permission = Node(LocalWorkspaceProjectionStore.StableNodeId("session_event", "permission-event"), "session_event", "permission-event",
            "permission", RootNodeId, 4, tokens) with
        {
            PermissionMetadata = new("not_observed", null, "not_observed", null),
        };
        return new([execution], [root, tool, subagent, skill, permission], [], [], [], [], null, null, "canonical", "registry");
    }

    private static LocalWorkspaceSessionDetailContribution CorruptSemantic(
        LocalWorkspaceSessionDetailContribution detail,
        string corruption)
    {
        var nodes = detail.Nodes.ToList();
        var tool = nodes[1];
        var subagent = nodes[2];
        var skill = nodes[3];
        var permission = nodes[4];
        nodes[1] = corruption switch
        {
            "source_kind_kind" => tool with { Kind = "event" },
            "uppercase_carrier" => tool with { SourceIdentity = new string('A', 64), NodeId = LocalWorkspaceProjectionStore.StableNodeId("semantic_tool", new string('A', 64)) },
            "missing_tool_metadata" => tool with { ToolMetadata = null },
            "extra_tool_metadata" => tool with { PermissionMetadata = new("not_observed", null, "not_observed", null) },
            "invalid_tool_state_value" => tool with { ToolMetadata = ToolMetadata() with { ExitState = "recorded", ExitCode = null } },
            "missing_semantic_references" => tool with { SourceReferences = [] },
            "overflow_references" => tool with
            {
                SourceReferences = Enumerable.Range(0, 17)
                    .Select(index => Ref("session_event", $"event-{index:D2}", null, null, $"event-{index:D2}"))
                    .ToArray(),
            },
            "duplicate_references" => tool with
            {
                SourceReferences = [Ref("session_event", "event-a", null, null, "event-a"), Ref("session_event", "event-a", null, null, "event-a")],
            },
            "unsorted_references" => tool with
            {
                SourceReferences = [Ref("session_event", "event-z", null, null, "event-z"), Ref("session_event", "event-a", null, null, "event-a")],
            },
            "empty_reference_identity" => tool with { SourceReferences = [Ref("session_event", null, null, null, null)] },
            "malformed_trace_span" => tool with { SourceReferences = [Ref("otel_span", "tool-event", "ABC", new string('d', 16), "tool-event")] },
            "mixed_tool_references" => tool with { SourceReferences = [Ref("otel_span", "tool-event", tool.TraceId, tool.SpanId, "tool-event"), Ref("session_event", "sdk-tool-event", null, null, "sdk-tool-event")] },
            "multiple_otel_tool_references" => tool with { SourceReferences = [Ref("otel_span", "tool-event", tool.TraceId, tool.SpanId, "tool-event"), Ref("otel_span", "tool-event-2", tool.TraceId, new string('e', 16), "tool-event-2")] },
            "otel_tool_digest" => tool with { SourceIdentity = new string('a', 64), NodeId = LocalWorkspaceProjectionStore.StableNodeId("semantic_tool", new string('a', 64)) },
            "tool_metadata_references" => tool with { ToolMetadata = tool.ToolMetadata! with { SourceReferences = [] } },
            "same_kind_foreign_reference" => tool with { SourceReferences = [Ref("otel_span", "foreign-tool-event", tool.TraceId, tool.SpanId, "foreign-tool-event", authorityValidated: false)] },
            "technical_reference_drift" => tool with { SourceReferences = [Ref("otel_span", "tool-event", tool.TraceId, tool.SpanId, "tool-event", revisionInput: "foreign-revision", authorityValidated: false)] },
            _ => tool,
        };
        if (corruption != "tool_metadata_references" && nodes[1].ToolMetadata is { } toolMetadata)
            nodes[1] = nodes[1] with { ToolMetadata = toolMetadata with { SourceReferences = nodes[1].SourceReferences } };
        nodes[2] = corruption switch
        {
            "missing_subagent_metadata" => subagent with { SubagentLifecycle = null },
            "invalid_subagent_lifecycle" => subagent with { SubagentLifecycle = subagent.SubagentLifecycle! with { FailedState = "completed" } },
            "subagent_reference_kind" => subagent with { SourceReferences = [Ref("otel_span", "subagent-event", new string('c', 32), new string('d', 16), "subagent-event")] },
            "subagent_metadata_references" => subagent with { SubagentLifecycle = subagent.SubagentLifecycle! with { SourceReferences = [] } },
            _ => subagent,
        };
        if (corruption != "subagent_metadata_references" && nodes[2].SubagentLifecycle is { } subagentLifecycle)
            nodes[2] = nodes[2] with { SubagentLifecycle = subagentLifecycle with { SourceReferences = nodes[2].SourceReferences } };
        nodes[3] = corruption switch
        {
            "invalid_skill_state_value" => skill with { SkillMetadata = skill.SkillMetadata! with { SourceState = "recorded", Source = null } },
            "skill_reference_kind" => skill with { SourceReferences = [Ref("session_event", "skill-source", null, null, "skill-source")] },
            "skill_reference_trace" => skill with { SourceReferences = [Ref("skill_claim", "skill-source", new string('a', 32), new string('b', 16), "skill-source")] },
            _ => skill,
        };
        nodes[4] = corruption switch
        {
            "invalid_permission_state_value" => permission with { PermissionMetadata = permission.PermissionMetadata! with { DecisionState = "recorded", Decision = null } },
            "raw_reference_kind" => permission with { SourceReferences = [Ref("skill_claim", permission.SourceIdentity, null, null, permission.EventId)] },
            "raw_reference_identity" => permission with { SourceReferences = [Ref("session_event", "other-event", null, null, "other-event")] },
            "raw_reference_trace" => permission with { SourceReferences = [Ref("session_event", permission.SourceIdentity, new string('a', 32), new string('b', 16), permission.EventId)] },
            _ => permission,
        };
        if (corruption == "root_reference_kind")
            nodes[0] = nodes[0] with { SourceReferences = [Ref("session_event", nodes[0].SourceIdentity, null, null, nodes[0].SourceIdentity)] };
        return detail with { Nodes = nodes };
    }

    private static LocalWorkspaceNodeSourceReferenceDetail Ref(
        string sourceKind, string? sourceIdentity, string? traceId, string? spanId, string? eventId,
        string revisionInput = "exact-reference", bool authorityValidated = true) =>
        new(sourceKind, sourceIdentity, traceId, spanId, eventId, revisionInput, authorityValidated);

    private static string SemanticDigest(string kind, string scope, string carrier)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-workspace-semantic-carrier\0v1\0"));
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(kind));
        hash.AppendData([0]);
        Append(scope);
        Append(carrier);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private static LocalWorkspaceToolMetadataDetail ToolMetadata(
        IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? references = null) =>
        new("not_observed", null, "recorded", "recorded", "not_observed",
            "source_unsupported", null, "not_observed", null, "source_unsupported", null,
            "recorded", "Read", "not_observed", "not_observed", "not_observed", null, references);

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

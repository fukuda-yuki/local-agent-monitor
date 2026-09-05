using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceOtelExecutionProjection
{
    private sealed record Span(string EventId, string RunId, string TraceId, string SpanId, string? ParentId, string? Operation, string? Status, LocalWorkspaceTokenFacts Tokens, long? Start, long? End, string? AgentName);
    private readonly Dictionary<string, Span> spans;
    private LocalWorkspaceOtelExecutionProjection(Dictionary<string, Span> spans) => this.spans = spans;

    internal static LocalWorkspaceOtelExecutionProjection? Read(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.event_id,e.run_id,m.trace_id,m.span_id,m.parent_span_id,m.operation,m.status,
              m.input_tokens,m.output_tokens,f.producer_total_tokens,m.reasoning_tokens,m.cache_read_tokens,m.cache_creation_tokens,
              local_workspace_ticks(m.start_time),local_workspace_ticks(m.end_time),m.agent_name
            FROM session_events e JOIN monitor_spans m ON e.trace_id=m.trace_id COLLATE BINARY
              AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
            LEFT JOIN local_workspace_span_facts f ON f.raw_record_id=m.raw_record_id AND f.span_ordinal=m.span_ordinal
            WHERE e.session_id=$session_id AND e.source_adapter='otel-exact' AND e.type='otel.span' AND e.run_id IS NOT NULL
              AND length(m.trace_id)=32 AND m.trace_id=lower(m.trace_id) AND m.trace_id NOT GLOB '*[^0-9a-f]*'
              AND length(m.span_id)=16 AND m.span_id=lower(m.span_id) AND m.span_id NOT GLOB '*[^0-9a-f]*'
              AND (SELECT COUNT(*) FROM monitor_spans other WHERE lower(other.trace_id)=m.trace_id AND lower(other.span_id)=m.span_id)=1
              AND (SELECT COUNT(*) FROM session_events other WHERE other.source_adapter='otel-exact'
                AND lower(other.source_event_id)=m.trace_id||'/'||m.span_id)=1
            LIMIT 4097;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, Span>(StringComparer.Ordinal);
        while (reader.Read())
        {
            LocalWorkspaceFact<long> Fact(int column) => reader.IsDBNull(column) ? new("not_observed", null) : new("recorded", reader.GetInt64(column));
            var span = new Span(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), new("llm_span", "recorded", 1, 1,
                    Fact(7), Fact(8), Fact(9), Fact(10), Fact(11), Fact(12), new("not_observed", null), new("not_observed", null)),
                    reader.IsDBNull(13) ? null : reader.GetInt64(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetString(15));
            result.Add(span.TraceId + "/" + span.SpanId, span);
        }
        if (result.Count > 4096) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
        return result.Count != 0 ? new(result) : null;
    }

    private Span? Parent(Span span) => span.ParentId is null ? null : spans.GetValueOrDefault(span.TraceId + "/" + span.ParentId);

    private Span? Agent(Span span)
    {
        Span? agent = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Span? current = span; current is not null; current = Parent(current))
        {
            if (!seen.Add(current.EventId)) return null;
            if (agent is null && current.Operation == "invoke_agent") agent = current;
        }
        return agent;
    }

    internal LocalWorkspaceSessionDetailContribution Apply(LocalWorkspaceSessionDetailContribution detail, LocalRepositorySessionDetailRequest request)
    {
        var executions = detail.Executions.ToDictionary(value => value.SourceIdentity, StringComparer.Ordinal);
        var runSpans = spans.Values.GroupBy(value => value.RunId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var owners = runSpans.ToDictionary(pair => pair.Key, pair => Agent(pair.Value)?.RunId ?? pair.Key, StringComparer.Ordinal);
        var executionOwners = detail.Executions.ToDictionary(value => value.ExecutionId,
            value => executions.GetValueOrDefault(owners.GetValueOrDefault(value.SourceIdentity, value.SourceIdentity)) ?? value,
            StringComparer.Ordinal);
        var originalRoots = detail.Nodes.Where(value => value.SourceKind == "execution_root").ToDictionary(value => value.ExecutionId);
        var semanticByEvent = detail.Nodes.Where(value => value.SourceKind == "semantic_tool")
            .SelectMany(node => (node.SourceReferences ?? []).Where(reference => reference.EventId is not null).Select(reference => (reference.EventId!, node)))
            .GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.First().node);
        var byEvent = detail.Nodes.Where(value => value.SourceKind == "session_event").ToDictionary(value => value.SourceIdentity);
        string? SpanNode(Span span) => semanticByEvent.GetValueOrDefault(span.EventId)?.NodeId ?? byEvent.GetValueOrDefault(span.EventId)?.NodeId;
        var meaningful = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in spans.Values.Where(value => value.Operation is "invoke_agent" or "chat" or "execute_tool" || value.Status == "error"))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (Span? current = span; current is not null && seen.Add(current.EventId); current = Parent(current))
                if (SpanNode(current) is { } nodeId) meaningful.Add(nodeId);
        }
        var retained = new List<LocalWorkspaceNodeDetail>();
        var unknownRoots = new Dictionary<string, LocalWorkspaceNodeDetail>(StringComparer.Ordinal);
        foreach (var node in detail.Nodes)
        {
            var owner = executionOwners[node.ExecutionId];
            if (node.SourceKind == "execution_root") continue;
            var originalExecution = detail.Executions.Single(value => value.ExecutionId == node.ExecutionId);
            var span = runSpans.GetValueOrDefault(originalExecution.SourceIdentity);
            if (span is null) { retained.Add(node); continue; }
            if (node.Kind == "unknown_relation_group") continue;
            if (node.SourceKind == "session_event" && node.SourceIdentity == span.EventId && !meaningful.Contains(node.NodeId)) continue;
            if (node.SourceKind == "session_event" && node.NameText is "otel.tool.input" or "otel.tool.result") continue;
            var root = originalRoots[owner.ExecutionId];
            var parent = node.SourceIdentity == span.EventId || node.SourceKind == "semantic_tool" ? Parent(span) : span;
            var parentId = parent is not null && owners.GetValueOrDefault(parent.RunId) == owner.SourceIdentity ? SpanNode(parent) : null;
            if (parentId == node.NodeId) parentId = null;
            var isSpan = node.SourceIdentity == span.EventId;
            var timeAuthority = span.Start is null ? span.End is null ? "missing" : "invalid"
                : span.End < span.Start || span.End is null && span.Status is "ok" or "error" ? "invalid" : "recorded";
            var unknown = parentId is null && span.Operation != "invoke_agent";
            if (unknown && !unknownRoots.ContainsKey(owner.ExecutionId))
                unknownRoots[owner.ExecutionId] = root with
                {
                    NodeId = LocalWorkspaceProjectionStore.StableNodeId("unknown_relation_group", owner.SourceIdentity),
                    SourceKind = "unknown_relation_group", SourceIdentity = owner.SourceIdentity,
                    Kind = "unknown_relation_group", RelationshipAuthority = "unknown", NameState = "not_observed", NameText = null,
                    SourceReferences = [],
                };
            var changed = node with
            {
                ExecutionId = owner.ExecutionId,
                ParentNodeId = parentId ?? (unknown ? unknownRoots[owner.ExecutionId].NodeId : root.NodeId),
                RelationshipAuthority = unknown ? "unknown" : "exact",
                Kind = isSpan && span.Operation == "invoke_agent" ? "agent" : isSpan && span.Status == "error" ? "error" : node.Kind,
                Tokens = isSpan ? LocalWorkspaceSessionSnapshotContributor.MergeCallTokens(node.Tokens, span.Tokens) : node.Tokens,
                NameState = isSpan && span.Operation == "invoke_agent" ? span.AgentName is null ? "not_observed" : "recorded" : node.NameState,
                NameText = isSpan && span.Operation == "invoke_agent" ? span.AgentName : node.NameText,
                TimeAuthority = isSpan ? timeAuthority : node.TimeAuthority,
                StartUtcTicks = isSpan ? timeAuthority == "recorded" ? span.Start : null : node.StartUtcTicks,
                EndUtcTicks = isSpan ? timeAuthority == "recorded" ? span.End : null : node.EndUtcTicks,
                DurationMilliseconds = isSpan ? timeAuthority == "recorded" ? (span.End - span.Start) / 10_000 : null : node.DurationMilliseconds,
                Status = isSpan ? span.Status == "error" ? "failed" : span.Status == "ok" ? "completed" : "unknown" : node.Status,
                Lifecycle = isSpan ? span.Status == "error" ? "failed" : span.Status == "ok" ? "completed" : "unknown" : node.Lifecycle,
                ToolMetadata = node.ToolMetadata is null ? null : node.ToolMetadata with { CallerState = parentId is null ? "not_observed" : "recorded", CallerNodeId = parentId },
            };
            retained.Add(changed);
        }
        retained.AddRange(unknownRoots.Values);
        var requiredExecutions = retained.Select(value => value.ExecutionId).ToHashSet(StringComparer.Ordinal);
        var grouped = new List<LocalWorkspaceExecutionDetail>();
        foreach (var owner in detail.Executions.Where(value => requiredExecutions.Contains(value.ExecutionId)))
        {
            var members = detail.Executions.Where(value => executionOwners[value.ExecutionId].ExecutionId == owner.ExecutionId).ToArray();
            LocalWorkspaceFact<long> Activity(Func<LocalWorkspaceActivityFacts, LocalWorkspaceFact<long>> select)
            {
                var facts = members.Select(value => select(value.Activity)).ToArray();
                var unavailable = facts.FirstOrDefault(value => value.State is not ("recorded" or "not_observed"));
                return unavailable ?? (facts.Any(value => value.State == "recorded") ? new("recorded", facts.Sum(value => value.Value ?? 0)) : new("not_observed", null));
            }
            var calls = members.Where(value => runSpans.GetValueOrDefault(value.SourceIdentity)?.Operation == "chat")
                .Select(value => LocalWorkspaceSessionSnapshotContributor.MergeCallTokens(value.Tokens, runSpans[value.SourceIdentity].Tokens)).ToArray();
            var activity = new LocalWorkspaceActivityFacts(Activity(value => value.Skill), Activity(value => value.Tool), Activity(value => value.Subagent), Activity(value => value.Error), Activity(value => value.Retry));
            var tokens = calls.Length == 0 ? owner.Tokens : LocalWorkspaceSessionSnapshotContributor.AggregateCalls(calls);
            var root = originalRoots[owner.ExecutionId] with { Activity = activity, Tokens = tokens };
            retained.Add(root);
            grouped.Add(owner with { Activity = activity, Tokens = tokens, Latest = false });
        }
        var children = retained.Where(value => value.ParentNodeId is not null).GroupBy(value => value.ParentNodeId!).ToDictionary(group => group.Key, group => (long)group.Count());
        retained = retained.Select(value => value with { ChildCount = children.GetValueOrDefault(value.NodeId), HasMoreChildren = children.GetValueOrDefault(value.NodeId) > 0, CollapsedChildren = new("complete", children.GetValueOrDefault(value.NodeId)) }).ToList();
        grouped = grouped.Select(value => value with { ChildCount = children.GetValueOrDefault(originalRoots[value.ExecutionId].NodeId) }).ToList();
        if (grouped.Count != 0) grouped[0] = grouped[0] with { Latest = true };
        if (request.NodeId is not null && retained.All(value => value.NodeId != request.NodeId)) throw new LocalWorkspaceSessionDetailException("node_not_found");
        if (request.ExecutionId is not null && grouped.All(value => value.ExecutionId != request.ExecutionId)) throw new LocalWorkspaceSessionDetailException("execution_not_found");
        var ids = retained.Select(value => value.NodeId).ToHashSet(StringComparer.Ordinal);
        return detail with
        {
            Executions = grouped, Nodes = retained,
            Edges = retained.Where(value => value.ParentNodeId is not null && value.RelationshipAuthority != "unknown").Select(value => new LocalWorkspaceNodeEdgeDetail(value.NodeId, value.ParentNodeId!, "parent", value.RelationshipAuthority, value.SourceOrdinal)).ToArray(),
            Content = detail.Content.Where(value => ids.Contains(value.NodeId)).ToArray(),
        };
    }
}

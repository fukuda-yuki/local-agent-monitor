using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Projection;

internal sealed record AgentExecutionGraph(
    AgentExecutionGraphSummary Summary,
    IReadOnlyList<AgentExecutionGraphSpan> Spans,
    IReadOnlyList<AgentExecutionGraphAgent> Agents,
    IReadOnlyList<AgentExecutionGraphSpanOwnership> SpanOwnership,
    IReadOnlyList<AgentExecutionGraphParallelGroup> ParallelGroups,
    IReadOnlyList<string> GraphWarnings);

internal sealed record AgentExecutionGraphSummary(
    string? MainAgentName,
    int RootAgentCount,
    int SubagentInvocationCount,
    int UniqueSubagentCount,
    int MaxAgentDepth,
    int ParallelAgentGroupCount,
    string RelationshipQuality,
    string AgentPresence);

internal sealed record AgentExecutionGraphSpan(
    string? SpanId,
    bool IsAgent,
    string? OwningAgentSpanId,
    string? ParentAgentSpanId,
    int? AgentDepth,
    string AgentRole,
    string RelationshipSource,
    string RelationshipConfidence);

internal sealed record AgentExecutionGraphAgent(
    string? SpanId,
    string? AgentName,
    string AgentRole,
    string? CallerAgentSpanId,
    string? Model,
    string? StartedAt,
    string? EndedAt,
    double? DurationMs,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    string? Status,
    int ChildAgentCount,
    int? AgentDepth,
    string RelationshipSource,
    string RelationshipConfidence);

internal sealed record AgentExecutionGraphSpanOwnership(
    string? SpanId,
    string? OwningAgentSpanId,
    string RelationshipSource,
    string RelationshipConfidence);

internal sealed record AgentExecutionGraphParallelGroup(IReadOnlyList<string> SpanIds);

internal static class AgentExecutionGraphBuilder
{
    private static readonly string[] WarningOrder = [
        "cycle_detected",
        "duplicate_span_id",
        "unknown_parent",
        "time_range_inconsistent",
    ];

    public static AgentExecutionGraph Build(IReadOnlyList<MonitorSpanRow> spans)
    {
        var states = spans.Select(span => new SpanState(span, IsAgent(span))).ToList();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var bySpanId = states
            .Where(state => !string.IsNullOrWhiteSpace(state.Row.SpanId))
            .GroupBy(state => state.Row.SpanId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var malformedEvidence = states.Any(state => string.IsNullOrWhiteSpace(state.Row.SpanId));

        foreach (var state in states)
        {
            state.Time = ParseTime(state.Row, warnings);
            if (!string.IsNullOrWhiteSpace(state.Row.SpanId)
                && bySpanId[state.Row.SpanId].Count > 1)
            {
                warnings.Add("duplicate_span_id");
            }
        }

        foreach (var state in states)
        {
            state.Relationship = ResolveRelationship(state, bySpanId, states, warnings);
            if (state.Relationship.Owner is { } owner
                && state.Time.IsValidRange
                && owner.Time.IsValidRange
                && !owner.Time.Contains(state.Time))
            {
                warnings.Add("time_range_inconsistent");
            }
        }

        NormalizeAgentCycles(states.Where(state => state.IsAgent).ToList(), warnings);

        var agents = states.Where(state => state.IsAgent).ToList();
        foreach (var agent in agents)
        {
            agent.AgentRole = ResolveAgentRole(agent, agents);
        }

        foreach (var agent in agents)
        {
            agent.AgentDepth = ResolveAgentDepth(agent, new HashSet<SpanState>());
        }

        foreach (var state in states.Where(state => !state.IsAgent))
        {
            state.AgentDepth = state.Relationship.Owner?.AgentDepth;
            state.AgentRole = state.Relationship.Owner?.AgentRole ?? "unknown";
        }

        var graphSpans = states.Select(state => new AgentExecutionGraphSpan(
            state.Row.SpanId,
            state.IsAgent,
            state.Relationship.Owner?.Row.SpanId,
            state.Relationship.Owner?.Relationship.Owner?.Row.SpanId,
            state.AgentDepth,
            state.AgentRole,
            state.Relationship.Source,
            state.Relationship.Confidence)).ToList();
        var agentDtos = agents.Select(agent => new AgentExecutionGraphAgent(
            agent.Row.SpanId,
            agent.Row.AgentName,
            agent.AgentRole,
            agent.Relationship.Owner?.Row.SpanId,
            agent.Row.ResponseModel ?? agent.Row.RequestModel,
            agent.Row.StartTime,
            agent.Row.EndTime,
            agent.Row.DurationMs,
            agent.Row.InputTokens,
            agent.Row.OutputTokens,
            agent.Row.TotalTokens,
            agent.Row.Status,
            agents.Count(child => ReferenceEquals(child.Relationship.Owner, agent)),
            agent.AgentDepth,
            agent.Relationship.Source,
            agent.Relationship.Confidence)).ToList();
        var ownership = states.Where(state => !state.IsAgent).Select(state => new AgentExecutionGraphSpanOwnership(
            state.Row.SpanId,
            state.Relationship.Owner?.Row.SpanId,
            state.Relationship.Source,
            state.Relationship.Confidence)).ToList();
        var parallelGroups = BuildParallelGroups(agents);
        var summary = BuildSummary(states, agents, parallelGroups, warnings, malformedEvidence);

        return new AgentExecutionGraph(
            summary,
            graphSpans,
            agentDtos,
            ownership,
            parallelGroups,
            WarningOrder.Where(warnings.Contains).ToList());
    }

    private static bool IsAgent(MonitorSpanRow span) =>
        string.Equals(span.Category, "agent_invocation", StringComparison.Ordinal)
        || string.Equals(span.Operation, "invoke_agent", StringComparison.Ordinal);

    private static SpanTime ParseTime(MonitorSpanRow span, ISet<string> warnings)
    {
        var hasStart = !string.IsNullOrWhiteSpace(span.StartTime);
        var hasEnd = !string.IsNullOrWhiteSpace(span.EndTime);
        var startParsed = DateTimeOffset.TryParse(span.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start);
        var endParsed = DateTimeOffset.TryParse(span.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end);
        if ((hasStart && !startParsed) || (hasEnd && !endParsed) || (startParsed && endParsed && start > end))
        {
            warnings.Add("time_range_inconsistent");
        }

        return new SpanTime(startParsed ? start : null, endParsed ? end : null, startParsed && endParsed && start <= end);
    }

    private static Relationship ResolveRelationship(
        SpanState state,
        IReadOnlyDictionary<string, List<SpanState>> bySpanId,
        IReadOnlyList<SpanState> states,
        ISet<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(state.Row.SpanId)
            || bySpanId.TryGetValue(state.Row.SpanId, out var ownMatches) && ownMatches.Count != 1)
        {
            return Relationship.Unresolved;
        }

        var parentSpanId = state.Row.ParentSpanId;
        if (string.IsNullOrWhiteSpace(parentSpanId))
        {
            return Relationship.Exact(null);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { state.Row.SpanId! };
        while (!string.IsNullOrWhiteSpace(parentSpanId))
        {
            if (!visited.Add(parentSpanId))
            {
                warnings.Add("cycle_detected");
                return Relationship.Unresolved;
            }

            if (!bySpanId.TryGetValue(parentSpanId, out var parentMatches))
            {
                warnings.Add("unknown_parent");
                return InferRelationship(state, states, bySpanId);
            }

            if (parentMatches.Count != 1)
            {
                warnings.Add("duplicate_span_id");
                return Relationship.Unresolved;
            }

            var parent = parentMatches[0];
            if (parent.IsAgent)
            {
                return Relationship.Exact(parent);
            }

            parentSpanId = parent.Row.ParentSpanId;
        }

        return Relationship.Exact(null);
    }

    private static Relationship InferRelationship(
        SpanState state,
        IReadOnlyList<SpanState> states,
        IReadOnlyDictionary<string, List<SpanState>> bySpanId)
    {
        if (!state.Time.IsValidRange)
        {
            return Relationship.Unresolved;
        }

        var candidates = states.Where(candidate =>
            !ReferenceEquals(candidate, state)
            && candidate.IsAgent
            && !string.IsNullOrWhiteSpace(candidate.Row.SpanId)
            && bySpanId[candidate.Row.SpanId].Count == 1
            && candidate.Time.Contains(state.Time)).ToList();
        return candidates.Count == 1
            ? Relationship.Inferred(candidates[0])
            : Relationship.Unresolved;
    }

    private static void NormalizeAgentCycles(IReadOnlyList<SpanState> agents, ISet<string> warnings)
    {
        foreach (var start in agents)
        {
            var path = new List<SpanState>();
            var pathIndexes = new Dictionary<SpanState, int>();
            SpanState? current = start;
            while (current is not null && !current.Relationship.IsUnresolved)
            {
                if (pathIndexes.TryGetValue(current, out var cycleStart))
                {
                    foreach (var cycleMember in path.Skip(cycleStart))
                    {
                        cycleMember.Relationship = Relationship.Unresolved;
                    }

                    warnings.Add("cycle_detected");
                    break;
                }

                pathIndexes.Add(current, path.Count);
                path.Add(current);
                current = current.Relationship.Owner;
            }
        }
    }

    private static string ResolveAgentRole(SpanState agent, IReadOnlyList<SpanState> agents)
    {
        if (agent.Relationship.IsUnresolved)
        {
            return "unknown";
        }

        if (agent.Relationship.Owner is not null)
        {
            return "sub";
        }

        var roots = agents.Where(candidate => !candidate.Relationship.IsUnresolved && candidate.Relationship.Owner is null).ToList();
        return roots.Count == 1 && ReferenceEquals(roots[0], agent) ? "main" : "root";
    }

    private static int? ResolveAgentDepth(SpanState agent, ISet<SpanState> visiting)
    {
        if (agent.AgentDepth is not null)
        {
            return agent.AgentDepth;
        }

        if (agent.Relationship.IsUnresolved || !visiting.Add(agent))
        {
            return null;
        }

        agent.AgentDepth = agent.Relationship.Owner is null
            ? 0
            : ResolveAgentDepth(agent.Relationship.Owner, visiting) is { } ownerDepth
                ? ownerDepth + 1
                : null;
        visiting.Remove(agent);
        return agent.AgentDepth;
    }

    private static IReadOnlyList<AgentExecutionGraphParallelGroup> BuildParallelGroups(IReadOnlyList<SpanState> agents)
    {
        var groups = new List<AgentExecutionGraphParallelGroup>();
        foreach (var siblings in agents
                     .Where(agent => !agent.Relationship.IsUnresolved && agent.Time.IsValidRange && agent.Row.SpanId is not null)
                     .GroupBy(agent => agent.Relationship.Owner, ReferenceEqualityComparer.Instance))
        {
            var ordered = siblings.OrderBy(agent => agent.Time.Start).ThenBy(agent => agent.Row.SpanId, StringComparer.Ordinal).ToList();
            var component = new List<SpanState>();
            DateTimeOffset? componentEnd = null;
            foreach (var agent in ordered)
            {
                if (component.Count > 0 && agent.Time.Start >= componentEnd)
                {
                    if (component.Count > 1)
                    {
                        groups.Add(new AgentExecutionGraphParallelGroup(component.Select(item => item.Row.SpanId!).ToList()));
                    }

                    component.Clear();
                    componentEnd = null;
                }

                component.Add(agent);
                componentEnd = componentEnd is null || agent.Time.End > componentEnd ? agent.Time.End : componentEnd;
            }

            if (component.Count > 1)
            {
                groups.Add(new AgentExecutionGraphParallelGroup(component.Select(item => item.Row.SpanId!).ToList()));
            }
        }

        return groups;
    }

    private static AgentExecutionGraphSummary BuildSummary(
        IReadOnlyList<SpanState> states,
        IReadOnlyList<SpanState> agents,
        IReadOnlyList<AgentExecutionGraphParallelGroup> parallelGroups,
        ISet<string> warnings,
        bool malformedEvidence)
    {
        var roots = agents.Where(agent => agent.AgentRole is "main" or "root").ToList();
        var subagents = agents.Where(agent => agent.AgentRole == "sub").ToList();
        var hasUnresolved = states.Any(state => state.Relationship.IsUnresolved) || malformedEvidence;
        var hasInference = states.Any(state => state.Relationship.Confidence == "inferred");
        var relationshipQuality = hasUnresolved
            ? "undeterminable"
            : hasInference
                ? "partially_inferred"
                : "exact";
        var agentPresence = agents.Count > 0
            ? "detected"
            : malformedEvidence || warnings.Count > 0
                ? "undeterminable"
                : "none_detected";
        var main = agents.SingleOrDefault(agent => agent.AgentRole == "main");

        return new AgentExecutionGraphSummary(
            main?.Row.AgentName,
            roots.Count,
            subagents.Count,
            subagents.Select(agent => agent.Row.AgentName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Count(),
            agents.Select(agent => agent.AgentDepth).Where(depth => depth is not null).Select(depth => depth!.Value).DefaultIfEmpty(0).Max(),
            parallelGroups.Count,
            relationshipQuality,
            agentPresence);
    }

    private sealed class SpanState
    {
        public SpanState(MonitorSpanRow row, bool isAgent)
        {
            Row = row;
            IsAgent = isAgent;
        }

        public MonitorSpanRow Row { get; }
        public bool IsAgent { get; }
        public SpanTime Time { get; set; }
        public Relationship Relationship { get; set; } = Relationship.Unresolved;
        public string AgentRole { get; set; } = "unknown";
        public int? AgentDepth { get; set; }
    }

    private readonly record struct SpanTime(DateTimeOffset? Start, DateTimeOffset? End, bool IsValidRange)
    {
        public bool Contains(SpanTime other) =>
            IsValidRange
            && other.IsValidRange
            && Start <= other.Start
            && End >= other.End;
    }

    private sealed record Relationship(SpanState? Owner, string Source, string Confidence)
    {
        public static Relationship Unresolved { get; } = new(null, "unresolved", "unknown");
        public bool IsUnresolved => Source == "unresolved";
        public static Relationship Exact(SpanState? owner) => new(owner, "parent_span", "exact");
        public static Relationship Inferred(SpanState owner) => new(owner, "time_inferred", "inferred");
    }
}

using System.Net;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class AgentExecutionGraphBuilderTests
{
    [Fact]
    public void Build_MainAgentOnly_ProducesExactMainSummary()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100, name: "main"),
        ]);

        var agent = Assert.Single(graph.Agents);
        Assert.Equal("main", agent.AgentRole);
        Assert.Null(agent.CallerAgentSpanId);
        Assert.Equal(0, agent.AgentDepth);
        Assert.Equal("exact", graph.Summary.RelationshipQuality);
        Assert.Equal("detected", graph.Summary.AgentPresence);
        Assert.Equal("main", graph.Summary.MainAgentName);
    }

    [Fact]
    public void Build_DetectsAgentsFromEitherCategoryOrOperation()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("by-category", null, agent: true, start: 0, end: 10) with { Operation = "other" },
            Span("by-operation", null, agent: true, start: 20, end: 30) with { Category = "other" },
        ]);

        Assert.Equal(2, graph.Agents.Count);
    }

    [Fact]
    public void Build_OneSubAgent_DerivesCallerAndDepth()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100, name: "main"),
            Span("sub", "main", agent: true, start: 10, end: 20, name: "worker"),
        ]);

        var sub = Assert.Single(graph.Agents, agent => agent.SpanId == "sub");
        Assert.Equal("sub", sub.AgentRole);
        Assert.Equal("main", sub.CallerAgentSpanId);
        Assert.Equal(1, sub.AgentDepth);
        Assert.Equal(1, graph.Summary.SubagentInvocationCount);
        Assert.Equal(1, graph.Summary.UniqueSubagentCount);
    }

    [Fact]
    public void Build_NestedSubAgent_DerivesDepthTwo()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("sub-1", "main", agent: true, start: 10, end: 90),
            Span("sub-2", "sub-1", agent: true, start: 20, end: 80),
        ]);

        var nested = Assert.Single(graph.Agents, agent => agent.SpanId == "sub-2");
        Assert.Equal(2, nested.AgentDepth);
        Assert.Equal(2, graph.Summary.MaxAgentDepth);
    }

    [Fact]
    public void Build_SerialSubAgents_DoNotCreateParallelGroup()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("first", "main", agent: true, start: 10, end: 30),
            Span("second", "main", agent: true, start: 40, end: 60),
        ]);

        Assert.Empty(graph.ParallelGroups);
        Assert.Equal(0, graph.Summary.ParallelAgentGroupCount);
    }

    [Fact]
    public void Build_ParallelSubAgents_CreateParallelGroup()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("first", "main", agent: true, start: 10, end: 40),
            Span("second", "main", agent: true, start: 20, end: 50),
        ]);

        var group = Assert.Single(graph.ParallelGroups);
        Assert.Equal(["first", "second"], group.SpanIds);
    }

    [Fact]
    public void Build_LlmAndToolUnderSubAgent_AreOwnedBySubAgent()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("sub", "main", agent: true, start: 10, end: 90),
            Span("llm", "sub", agent: false, start: 20, end: 30),
            Span("tool", "llm", agent: false, start: 31, end: 40),
        ]);

        Assert.All(graph.SpanOwnership, ownership => Assert.Equal("sub", ownership.OwningAgentSpanId));
        Assert.All(graph.SpanOwnership, ownership => Assert.Equal("exact", ownership.RelationshipConfidence));
    }

    [Fact]
    public void Build_DerivesFullPerSpanOwnershipModel()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("sub", "main", agent: true, start: 10, end: 90),
            Span("tool", "sub", agent: false, start: 20, end: 30),
        ]);

        var tool = Assert.Single(graph.Spans, span => span.SpanId == "tool");
        Assert.Equal("sub", tool.OwningAgentSpanId);
        Assert.Equal("main", tool.ParentAgentSpanId);
        Assert.Equal(1, tool.AgentDepth);
        Assert.Equal("sub", tool.AgentRole);
    }

    [Fact]
    public void Build_DirectAgentOwnerRemainsExactWhenHigherAgentChainCycles()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("owner", "cycle", agent: true, start: 0, end: 100),
            Span("cycle", "owner", agent: true, start: 0, end: 100),
            Span("tool", "owner", agent: false, start: 10, end: 20),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Equal("owner", ownership.OwningAgentSpanId);
        Assert.Equal("parent_span", ownership.RelationshipSource);
        Assert.Equal("exact", ownership.RelationshipConfidence);
        Assert.Contains("cycle_detected", graph.GraphWarnings);
    }

    [Fact]
    public void Build_MissingParentWithOneTimeContainer_InfersOwnership()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("orphan", "missing", agent: false, start: 20, end: 30),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Equal("main", ownership.OwningAgentSpanId);
        Assert.Equal("time_inferred", ownership.RelationshipSource);
        Assert.Equal("inferred", ownership.RelationshipConfidence);
        Assert.Equal("partially_inferred", graph.Summary.RelationshipQuality);
        Assert.Contains("unknown_parent", graph.GraphWarnings);
    }

    [Fact]
    public void Build_ClaudeMissingParent_DoesNotInferTimeRangeOwnership()
    {
        var graph = AgentExecutionGraphBuilder.Build(
        [
            Span("main", null, agent: true, start: 0, end: 100, rawRecordId: 42),
            Span("orphan", "missing", agent: false, start: 20, end: 30, rawRecordId: 42),
        ],
        new HashSet<long> { 42 });

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("unknown", ownership.RelationshipConfidence);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
        Assert.Contains("unknown_parent", graph.GraphWarnings);
    }

    [Fact]
    public void Build_MixedSourceRows_ApplyExactPolicyOnlyToClaudeRecordIds()
    {
        var graph = AgentExecutionGraphBuilder.Build(
        [
            Span("claude-agent", null, agent: true, start: 0, end: 100, rawRecordId: 42),
            Span("claude-orphan", "missing-claude", agent: false, start: 20, end: 30, rawRecordId: 42),
            Span("copilot-agent", null, agent: true, start: 200, end: 300, rawRecordId: 99),
            Span("copilot-orphan", "missing-copilot", agent: false, start: 220, end: 230, rawRecordId: 99),
        ],
        new HashSet<long> { 42 });

        var claude = Assert.Single(graph.SpanOwnership, item => item.SpanId == "claude-orphan");
        Assert.Null(claude.OwningAgentSpanId);
        Assert.Equal("unresolved", claude.RelationshipSource);
        Assert.Equal("unknown", claude.RelationshipConfidence);

        var copilot = Assert.Single(graph.SpanOwnership, item => item.SpanId == "copilot-orphan");
        Assert.Equal("copilot-agent", copilot.OwningAgentSpanId);
        Assert.Equal("time_inferred", copilot.RelationshipSource);
        Assert.Equal("inferred", copilot.RelationshipConfidence);
    }

    [Fact]
    public void Build_ClaudeDuplicateParentEvidence_RemainsUnresolved()
    {
        var graph = AgentExecutionGraphBuilder.Build(
        [
            Span("duplicate", null, agent: true, start: 0, end: 100, rawRecordId: 42),
            Span("duplicate", null, agent: true, start: 0, end: 100, rawRecordId: 42),
            Span("orphan", "duplicate", agent: false, start: 20, end: 30, rawRecordId: 42),
        ],
        new HashSet<long> { 42 });

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("unknown", ownership.RelationshipConfidence);
        Assert.Contains("duplicate_span_id", graph.GraphWarnings);
    }

    [Fact]
    public void Build_ClaudeCyclicParentEvidence_RemainsUnresolved()
    {
        var graph = AgentExecutionGraphBuilder.Build(
        [
            Span("first", "second", agent: true, start: 0, end: 100, rawRecordId: 42),
            Span("second", "first", agent: true, start: 10, end: 90, rawRecordId: 42),
        ],
        new HashSet<long> { 42 });

        Assert.All(graph.Agents, agent =>
        {
            Assert.Equal("unknown", agent.AgentRole);
            Assert.Null(agent.AgentDepth);
            Assert.Equal("unresolved", agent.RelationshipSource);
            Assert.Equal("unknown", agent.RelationshipConfidence);
        });
        Assert.Contains("cycle_detected", graph.GraphWarnings);
    }

    [Fact]
    public void Build_AgentWithMissingParent_DoesNotInferItselfAsItsOwner()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 100),
            Span("sub", "missing", agent: true, start: 20, end: 30),
        ]);

        var sub = Assert.Single(graph.Agents, agent => agent.SpanId == "sub");
        Assert.Equal("main", sub.CallerAgentSpanId);
        Assert.Equal("sub", sub.AgentRole);
        Assert.Equal("inferred", sub.RelationshipConfidence);
    }

    [Fact]
    public void Build_MissingParentWithMultipleTimeContainers_IsUnresolved()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("first", null, agent: true, start: 0, end: 100),
            Span("second", null, agent: true, start: 0, end: 100),
            Span("orphan", "missing", agent: false, start: 20, end: 30),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
    }

    [Fact]
    public void Build_MissingParentDoesNotInferAgentWithBlankSpanId()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("", null, agent: true, start: 0, end: 100),
            Span("orphan", "missing", agent: false, start: 20, end: 30),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("unknown", ownership.RelationshipConfidence);
    }

    [Fact]
    public void Build_MissingParentDoesNotInferAgentWithDuplicateSpanId()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("duplicate", null, agent: true, start: 0, end: 100),
            Span("duplicate", null, agent: true, start: 200, end: 300),
            Span("orphan", "missing", agent: false, start: 20, end: 30),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("unknown", ownership.RelationshipConfidence);
        Assert.Contains("duplicate_span_id", graph.GraphWarnings);
    }

    [Fact]
    public void Build_MutuallyInferredAgents_AreNormalizedToUndeterminableCycle()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("first", "missing-first", agent: true, start: 0, end: 100),
            Span("second", "missing-second", agent: true, start: 0, end: 100),
        ]);

        Assert.All(graph.Agents, agent =>
        {
            Assert.Null(agent.CallerAgentSpanId);
            Assert.Equal("unknown", agent.AgentRole);
            Assert.Null(agent.AgentDepth);
            Assert.Equal("unresolved", agent.RelationshipSource);
            Assert.Equal("unknown", agent.RelationshipConfidence);
        });
        Assert.Contains("cycle_detected", graph.GraphWarnings);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
    }

    [Fact]
    public void Build_UnknownParent_EmitsWarningWithoutThrowing()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 0, end: 10),
            Span("orphan", "missing", agent: false, start: null, end: null),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Contains("unknown_parent", graph.GraphWarnings);
    }

    [Fact]
    public void Build_ChildOutsideOwningAgentTimeRange_EmitsWarning()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("main", null, agent: true, start: 10, end: 20),
            Span("tool", "main", agent: false, start: 0, end: 5),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Equal("main", ownership.OwningAgentSpanId);
        Assert.Equal("exact", ownership.RelationshipConfidence);
        Assert.Contains("time_range_inconsistent", graph.GraphWarnings);
    }

    [Fact]
    public void Build_CycleAndDuplicateIds_AreBoundedAndUndeterminable()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("cycle-a", "cycle-b", agent: true, start: 0, end: 100),
            Span("cycle-b", "cycle-a", agent: false, start: 10, end: 20),
            Span("duplicate", null, agent: true, start: 0, end: 100),
            Span("duplicate", null, agent: false, start: 10, end: 20),
        ]);

        Assert.Contains("cycle_detected", graph.GraphWarnings);
        Assert.Contains("duplicate_span_id", graph.GraphWarnings);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
    }

    [Fact]
    public void Build_GraphWarnings_UseBoundedDeterministicOrder()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("duplicate", null, agent: true, start: 10, end: 0),
            Span("duplicate", null, agent: false, start: 0, end: 10),
            Span("cycle-a", "cycle-b", agent: false, start: 0, end: 10),
            Span("cycle-b", "cycle-a", agent: false, start: 0, end: 10),
            Span("orphan", "missing", agent: false, start: null, end: null),
        ]);

        Assert.Equal([
            "cycle_detected",
            "duplicate_span_id",
            "unknown_parent",
            "time_range_inconsistent",
        ], graph.GraphWarnings);
    }

    [Fact]
    public void Build_MultipleRootAgents_DoesNotFabricateMain()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("first", null, agent: true, start: 0, end: 10, name: "one"),
            Span("second", null, agent: true, start: 20, end: 30, name: "two"),
        ]);

        Assert.All(graph.Agents, agent => Assert.Equal("root", agent.AgentRole));
        Assert.Null(graph.Summary.MainAgentName);
        Assert.Equal(2, graph.Summary.RootAgentCount);
    }

    [Fact]
    public void Build_NoAgentSpans_ReportsNoneDetectedAndZeroSummary()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("llm", null, agent: false, start: 0, end: 10),
        ]);

        Assert.Empty(graph.Agents);
        Assert.Equal("none_detected", graph.Summary.AgentPresence);
        Assert.Equal(0, graph.Summary.RootAgentCount);
        Assert.Equal(0, graph.Summary.SubagentInvocationCount);
        Assert.Equal(0, graph.Summary.MaxAgentDepth);
    }

    [Fact]
    public void Build_NoAgentSpansWithBrokenParentChain_ReportsUndeterminable()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("orphan", "missing", agent: false, start: null, end: null),
        ]);

        Assert.Empty(graph.Agents);
        Assert.Equal("undeterminable", graph.Summary.AgentPresence);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
    }

    [Fact]
    public void Build_BlankSpanId_DoesNotThrowAndReportsUndeterminableEvidence()
    {
        var graph = AgentExecutionGraphBuilder.Build([
            Span("", null, agent: false, start: 0, end: 10),
        ]);

        var ownership = Assert.Single(graph.SpanOwnership);
        Assert.Null(ownership.OwningAgentSpanId);
        Assert.Equal("unresolved", ownership.RelationshipSource);
        Assert.Equal("undeterminable", graph.Summary.AgentPresence);
        Assert.Equal("undeterminable", graph.Summary.RelationshipQuality);
    }

    private static MonitorSpanRow Span(string spanId, string? parentSpanId, bool agent, int? start, int? end, string? name = null, long rawRecordId = 0) =>
        new(
            Id: 0,
            RawRecordId: rawRecordId,
            TraceId: "graph-trace",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            SpanOrdinal: 0,
            Operation: agent ? "invoke_agent" : "chat",
            Category: agent ? "agent_invocation" : "llm_call",
            ToolName: null,
            ToolType: null,
            McpToolName: null,
            McpServerHash: null,
            AgentName: name,
            RequestModel: "gpt-test",
            ResponseModel: null,
            InputTokens: 10,
            OutputTokens: 5,
            TotalTokens: 15,
            ReasoningTokens: null,
            CacheReadTokens: null,
            CacheCreationTokens: null,
            Status: "ok",
            ErrorType: null,
            FinishReasons: null,
            ConversationId: null,
            DurationMs: start is null || end is null ? null : end - start,
            StartTime: start is null ? null : DateTimeOffset.UnixEpoch.AddSeconds(start.Value).ToString("O"),
            EndTime: end is null ? null : DateTimeOffset.UnixEpoch.AddSeconds(end.Value).ToString("O"),
            ProjectedAt: DateTimeOffset.UnixEpoch.ToString("O"));
}

public class AgentExecutionGraphEndpointTests
{
    [Fact]
    public async Task AgentGraph_ReturnsSpecifiedSanitizedShape()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, "agent-graph", Payload("agent-graph"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync("/api/monitor/traces/agent-graph/agent-graph");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        AssertExactFields(root, "summary", "agents", "span_ownership", "parallel_groups", "graph_warnings");
        AssertExactFields(root.GetProperty("summary"), "main_agent_name", "root_agent_count", "subagent_invocation_count", "unique_subagent_count", "max_agent_depth", "parallel_agent_group_count", "relationship_quality", "agent_presence");
        AssertExactFields(root.GetProperty("agents")[0], "span_id", "agent_name", "agent_role", "caller_agent_span_id", "model", "started_at", "ended_at", "duration_ms", "input_tokens", "output_tokens", "total_tokens", "status", "child_agent_count", "agent_depth", "relationship_source", "relationship_confidence");
        AssertExactFields(root.GetProperty("span_ownership")[0], "span_id", "owning_agent_span_id", "relationship_source", "relationship_confidence");
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", json);
        Assert.DoesNotContain("SECRET_ATTRIBUTE_PAYLOAD_MARKER", json);
        Assert.DoesNotContain("attributes", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentGraph_UnderSanitizedOnly_RemainsAvailableWithoutRawContent()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, "sanitized-agent-graph", Payload("sanitized-agent-graph"));
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync("/api/monitor/traces/sanitized-agent-graph/agent-graph");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("main", document.RootElement.GetProperty("agents")[0].GetProperty("agent_role").GetString());
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", json);
        Assert.DoesNotContain("SECRET_ATTRIBUTE_PAYLOAD_MARKER", json);
    }

    [Fact]
    public async Task AgentGraph_NestedParallelAgents_ReturnsExactSemanticValues()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, "nested-parallel", NestedParallelPayload("nested-parallel"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync("/api/monitor/traces/nested-parallel/agent-graph");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        Assert.Equal("main", summary.GetProperty("main_agent_name").GetString());
        Assert.Equal(1, summary.GetProperty("root_agent_count").GetInt32());
        Assert.Equal(3, summary.GetProperty("subagent_invocation_count").GetInt32());
        Assert.Equal(3, summary.GetProperty("unique_subagent_count").GetInt32());
        Assert.Equal(2, summary.GetProperty("max_agent_depth").GetInt32());
        Assert.Equal(1, summary.GetProperty("parallel_agent_group_count").GetInt32());
        Assert.Equal("exact", summary.GetProperty("relationship_quality").GetString());
        Assert.Equal("detected", summary.GetProperty("agent_presence").GetString());

        var agents = root.GetProperty("agents");
        Assert.Equal(4, agents.GetArrayLength());
        AssertAgent(agents[0], "main", "main", caller: null, depth: 0, childCount: 1);
        AssertAgent(agents[1], "coordinator", "sub", caller: "main", depth: 1, childCount: 2);
        AssertAgent(agents[2], "first", "sub", caller: "coordinator", depth: 2, childCount: 0);
        AssertAgent(agents[3], "second", "sub", caller: "coordinator", depth: 2, childCount: 0);
        var ownership = Assert.Single(root.GetProperty("span_ownership").EnumerateArray());
        Assert.Equal("tool", ownership.GetProperty("span_id").GetString());
        Assert.Equal("first", ownership.GetProperty("owning_agent_span_id").GetString());
        Assert.Equal("parent_span", ownership.GetProperty("relationship_source").GetString());
        Assert.Equal("exact", ownership.GetProperty("relationship_confidence").GetString());
        Assert.Equal("[[\"first\",\"second\"]]", root.GetProperty("parallel_groups").GetRawText());
        Assert.Equal("[]", root.GetProperty("graph_warnings").GetRawText());
    }

    [Fact]
    public async Task AgentGraph_UnknownTraceReturnsTraceNotFound()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync("/api/monitor/traces/missing/agent-graph");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.False(root.GetProperty("accepted").GetBoolean());
        Assert.Equal("trace_not_found", root.GetProperty("error").GetString());
        Assert.Equal("The requested trace was not found.", root.GetProperty("message").GetString());
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task AgentGraph_PersistenceBusyReturnsFixedFailureShape()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            ProjectionStore = new BusyProjectionStore(),
            StartWriter = false,
            StartProjectionWorker = false,
        });

        var response = await host.Client.GetAsync("/api/monitor/traces/trace/agent-graph");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.False(root.GetProperty("accepted").GetBoolean());
        Assert.Equal("persistence_busy", root.GetProperty("error").GetString());
        Assert.Equal("The local monitor raw store is busy.", root.GetProperty("message").GetString());
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    private static void Seed(MonitorTempDirectory temp, string traceId, string payload)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, traceId, DateTimeOffset.UnixEpoch, null, payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch);
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch);
    }

    private static void AssertExactFields(JsonElement element, params string[] fields)
    {
        Assert.Equal(fields.Length, element.EnumerateObject().Count());
        foreach (var field in fields)
        {
            Assert.True(element.TryGetProperty(field, out _), $"Missing field: {field}");
        }
    }

    private static void AssertAgent(JsonElement agent, string spanId, string role, string? caller, int depth, int childCount)
    {
        Assert.Equal(spanId, agent.GetProperty("span_id").GetString());
        Assert.Equal(spanId, agent.GetProperty("agent_name").GetString());
        Assert.Equal(role, agent.GetProperty("agent_role").GetString());
        if (caller is null)
        {
            Assert.Equal(JsonValueKind.Null, agent.GetProperty("caller_agent_span_id").ValueKind);
        }
        else
        {
            Assert.Equal(caller, agent.GetProperty("caller_agent_span_id").GetString());
        }

        Assert.Equal(JsonValueKind.Null, agent.GetProperty("model").ValueKind);
        Assert.Equal(depth, agent.GetProperty("agent_depth").GetInt32());
        Assert.Equal(childCount, agent.GetProperty("child_agent_count").GetInt32());
        Assert.Equal("parent_span", agent.GetProperty("relationship_source").GetString());
        Assert.Equal("exact", agent.GetProperty("relationship_confidence").GetString());
    }

    private static string Payload(string traceId) => """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"__TRACE__","spanId":"main","name":"invoke_agent","startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"main"}},{"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}}]},
          {"traceId":"__TRACE__","spanId":"llm","parentSpanId":"main","name":"chat","startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},{"key":"unsafe.attribute","value":{"stringValue":"SECRET_ATTRIBUTE_PAYLOAD_MARKER"}}]}
        ]}]}]}
        """.Replace("__TRACE__", traceId, StringComparison.Ordinal);

    private static string NestedParallelPayload(string traceId) => """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"__TRACE__","spanId":"main","name":"invoke_agent","startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000100000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"main"}}]},
          {"traceId":"__TRACE__","spanId":"coordinator","parentSpanId":"main","name":"invoke_agent","startTimeUnixNano":"1710000010000000000","endTimeUnixNano":"1710000090000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"coordinator"}}]},
          {"traceId":"__TRACE__","spanId":"first","parentSpanId":"coordinator","name":"invoke_agent","startTimeUnixNano":"1710000020000000000","endTimeUnixNano":"1710000060000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"first"}}]},
          {"traceId":"__TRACE__","spanId":"second","parentSpanId":"coordinator","name":"invoke_agent","startTimeUnixNano":"1710000030000000000","endTimeUnixNano":"1710000070000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"second"}}]},
          {"traceId":"__TRACE__","spanId":"tool","parentSpanId":"first","name":"execute_tool","startTimeUnixNano":"1710000040000000000","endTimeUnixNano":"1710000050000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]}
        ]}]}]}
        """.Replace("__TRACE__", traceId, StringComparison.Ordinal);

    private sealed class BusyProjectionStore : IMonitorProjectionStore
    {
        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) => throw new NotSupportedException();
        public bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt) => throw new NotSupportedException();
        public MonitorProjectionStatus GetProjectionStatus() => throw new NotSupportedException();
        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) => throw new NotSupportedException();
        public bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) => throw new NotSupportedException();
        public MonitorProjectionStatus GetSpanProjectionStatus() => throw new NotSupportedException();
        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) => throw new NotSupportedException();
        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) => throw new NotSupportedException();
        public MonitorTraceRow? GetMonitorTrace(string traceId) => throw new PersistenceBusyException();
        public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) => throw new NotSupportedException();
        public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => throw new NotSupportedException();
        public RawTelemetryRecord? GetRawRecordById(long id) => throw new NotSupportedException();
        public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit) => throw new NotSupportedException();
        public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) => throw new NotSupportedException();
        public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) => throw new NotSupportedException();
        public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) => throw new NotSupportedException();
        public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) => throw new NotSupportedException();
        public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) => throw new NotSupportedException();
        public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) => throw new NotSupportedException();
        public MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) => throw new NotSupportedException();
        public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) => throw new NotSupportedException();
    }
}

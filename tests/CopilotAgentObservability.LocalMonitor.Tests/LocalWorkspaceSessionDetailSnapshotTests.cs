using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionDetailSnapshotTests
{
    [Fact]
    public async Task ProductionCoordinatorRejectsForeignSameKindSdkToolReference()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='session_sdk';").Single();
            using var mutation = connection.CreateCommand();
            mutation.CommandText = "UPDATE local_workspace_node_source_references SET source_identity='0198f5b8-0c00-7000-8000-000000000099',event_id='0198f5b8-0c00-7000-8000-000000000099' WHERE node_id=$node;";
            mutation.Parameters.AddWithValue("$node", nodeId);
            Assert.True(mutation.ExecuteNonQuery() > 0);
        }
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("otel_same_kind_foreign", "UPDATE local_workspace_node_source_references SET source_identity='018f0000-0000-7000-8000-000000000099',event_id='018f0000-0000-7000-8000-000000000099' WHERE source_kind='otel_span';")]
    [InlineData("otel_technical_trace_drift", "UPDATE local_workspace_node_source_references SET trace_id='cccccccccccccccccccccccccccccccc' WHERE source_kind='otel_span';")]
    [InlineData("sdk_subagent_foreign_lifecycle", "UPDATE local_workspace_node_source_references SET source_identity='018f0000-0000-7000-8000-000000000011',event_id='018f0000-0000-7000-8000-000000000011' WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent') AND source_ordinal=0;")]
    public async Task ProductionCoordinatorRejectsForeignSameKindAndTechnicalSemanticReferences(string corruption, string mutation)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = mutation;
            Assert.True(command.ExecuteNonQuery() > 0);
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                corruption.StartsWith("sdk_", StringComparison.Ordinal)
                    ? "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent';"
                    : "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='otel';").Single();
        }
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorValidatesExactOtelToolExecutionActivity()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string otelRunId = "018f0000-0000-7000-8000-000000000010";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId, otelRunId,
            "018f0000-0000-7000-8000-000000000020");

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        var execution = Assert.Single(detail.Detail.Executions, value => value.SourceIdentity == otelRunId);
        Assert.Equal("recorded", execution.Activity.Tool.State);
        Assert.Equal(1, execution.Activity.Tool.Value);
    }

    [Fact]
    public async Task ProductionCoordinatorKeepsAStandaloneSdkToolStartAsARawEvent()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string rawNodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                "DELETE FROM session_events WHERE event_id='018f0000-0000-7000-8000-000000000032';");
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            Assert.Equal(["0"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='session_sdk';
                """));
            rawNodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_nodes
                WHERE source_kind='session_event' AND source_identity='018f0000-0000-7000-8000-000000000031';
                """).Single();
        }

        var service = CreateRoundFiveService(temp.DatabasePath);
        await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        var node = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: rawNodeId), CancellationToken.None);

        var raw = Assert.Single(node.Detail.Nodes, value => value.NodeId == rawNodeId);
        Assert.Equal("session_event", raw.SourceKind);
        Assert.Equal("event", raw.Kind);
    }

    [Fact]
    public async Task ProductionCoordinatorKeepsSdkToolOutcomeUnknownWithoutOtelOrHookAuthority()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='session_sdk';
                """).Single();

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal("completed", tool.Lifecycle);
        Assert.Equal("unknown", tool.Status);
    }

    [Fact]
    public async Task ProductionCoordinatorRejectsCoherentOtelToolExecutionActivityDrift()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string otelRunId = "018f0000-0000-7000-8000-000000000010";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId, otelRunId,
            "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                UPDATE local_workspace_execution_headers
                SET tool_activity_state='not_observed',tool_activity_count=NULL
                WHERE session_id='{{sessionId}}' AND source_identity='{{otelRunId}}';
                UPDATE local_workspace_nodes
                SET tool_activity_state='not_observed',tool_activity_count=NULL
                WHERE session_id='{{sessionId}}' AND source_kind='execution_root' AND source_identity='{{otelRunId}}';
                """);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() =>
            CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorAcceptsCanonicalSdkToolOverflowSnapshot()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string otelRunId = "018f0000-0000-7000-8000-000000000010";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        const string startEventId = "018f0000-0000-7000-8000-000000000031";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId, otelRunId, sdkRunId);
        using (var connection = OpenFile(temp.DatabasePath))
        {
            for (var index = 0; index < 15; index++)
            {
                var eventId = index == 0
                    ? "018f0000-0000-7000-8000-000000000030"
                    : $"018f0000-0000-7000-8000-{index + 100:D12}";
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO session_events(
                      event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                      parent_event_id,type,occurred_at,content_state)
                    VALUES($event,$session,$run,'copilot-sdk','copilot-sdk-stream',$source,$parent,
                      'tool.execution_complete',$occurred,'not_captured');
                    """;
                insert.Parameters.AddWithValue("$event", eventId);
                insert.Parameters.AddWithValue("$session", sessionId);
                insert.Parameters.AddWithValue("$run", sdkRunId);
                insert.Parameters.AddWithValue("$source", $"sdk-tool-overflow-{index:D2}");
                insert.Parameters.AddWithValue("$parent", startEventId);
                insert.Parameters.AddWithValue("$occurred", $"2026-08-26T00:00:{index + 10:D2}.0000000+00:00");
                Assert.Equal(1, insert.ExecuteNonQuery());
            }
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(
                connection,
                transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:00Z"),
                FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();

            Assert.Equal(["inconsistent:inconsistent:inconsistent:16"],
                LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                    SELECT metadata.started_state||':'||metadata.completed_state||':'||metadata.failed_state||':'||COUNT(reference.source_ordinal)
                    FROM local_workspace_tool_metadata metadata
                    JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=metadata.node_id
                    JOIN local_workspace_node_source_references reference ON reference.node_id=metadata.node_id
                    WHERE receipt.source_family='session_sdk'
                    GROUP BY metadata.node_id;
                    """));
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        var execution = Assert.Single(detail.Detail.Executions, value => value.SourceIdentity == sdkRunId);
        Assert.Equal("recorded", execution.Activity.Tool.State);
        Assert.Equal(1, execution.Activity.Tool.Value);
    }

    [Fact]
    public async Task ProductionCoordinatorReadsAuthorizedOtelToolAndSdkSubagentAcrossEveryDetailShape()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string otelRunId = "018f0000-0000-7000-8000-000000000010";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId, otelRunId, sdkRunId);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);

        var summary = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        Assert.Equal(2, summary.Detail.Executions.Count);
        string toolNodeId;
        string subagentNodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            toolNodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='otel';").Single();
            subagentNodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent';").Single();
        }
        var toolRead = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: toolNodeId), CancellationToken.None);
        var subagentRead = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: subagentNodeId), CancellationToken.None);
        var tool = Assert.Single(toolRead.Detail.Nodes, node => node.NodeId == toolNodeId);
        var subagent = Assert.Single(subagentRead.Detail.Nodes, node => node.NodeId == subagentNodeId);
        Assert.Equal("tool", tool.Kind);
        Assert.NotNull(tool.ToolMetadata);
        Assert.Null(tool.SubagentLifecycle);
        Assert.Equal(new string('a', 32), tool.TraceId);
        Assert.Equal(new string('b', 16), tool.SpanId);
        Assert.Equal("018f0000-0000-7000-8000-000000000011", tool.EventId);
        Assert.Equal("recorded", tool.TimeAuthority);
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T00:00:02Z").UtcTicks, tool.StartUtcTicks);
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T00:00:04Z").UtcTicks, tool.EndUtcTicks);
        Assert.Equal(2000, tool.DurationMilliseconds);
        Assert.Equal("recorded", tool.ToolMetadata!.McpServerIdentityState);
        Assert.Equal(new string('d', 64), tool.ToolMetadata.McpServerIdentity);
        Assert.Equal("recorded", tool.ToolMetadata.McpToolNameState);
        Assert.Equal("ReadMcp", tool.ToolMetadata.McpToolName);
        Assert.Equal("subagent", subagent.Kind);
        Assert.NotNull(subagent.SubagentLifecycle);
        Assert.Null(subagent.ToolMetadata);

        foreach (var pair in new[] { (RunId: otelRunId, Node: tool), (RunId: sdkRunId, Node: subagent) })
        {
            var execution = Assert.Single(summary.Detail.Executions, value => value.SourceIdentity == pair.RunId);
            var timeline = await service.ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Timeline, sessionId, ExecutionId: execution.ExecutionId),
                CancellationToken.None);
            Assert.Contains(timeline.Detail.Nodes, value => value.NodeId == pair.Node.NodeId);
            Assert.Contains(pair.Node.NodeId == toolNodeId ? toolRead.Detail.Nodes : subagentRead.Detail.Nodes,
                value => value.NodeId == pair.Node.NodeId);
        }

        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE monitor_spans SET end_time='2026-08-26T00:00:05.0000000+00:00',duration_ms=3000
                WHERE trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';
                """);
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-26T00:10:01Z"));
        }
        var changed = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        Assert.NotEqual(summary.WorkspaceRevision, changed.WorkspaceRevision);
        var changedTool = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: toolNodeId), CancellationToken.None);
        Assert.Equal(3000, Assert.Single(changedTool.Detail.Nodes, node => node.NodeId == toolNodeId).DurationMilliseconds);
    }

    [Theory]
    [InlineData("DELETE FROM monitor_spans WHERE trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';")]
    [InlineData("INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at) SELECT 2,trace_id,span_id,parent_span_id,1,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at FROM monitor_spans WHERE raw_record_id=1;")]
    [InlineData("UPDATE monitor_spans SET operation='chat' WHERE raw_record_id=1;")]
    [InlineData("UPDATE monitor_spans SET status='error' WHERE raw_record_id=1;")]
    [InlineData("UPDATE monitor_spans SET start_time='2026-08-26T00:00:01.0000000+00:00' WHERE raw_record_id=1;")]
    [InlineData("UPDATE monitor_spans SET mcp_tool_name='ForgedMcp' WHERE raw_record_id=1;")]
    [InlineData("UPDATE monitor_spans SET mcp_server_hash=lower(hex(randomblob(32))) WHERE raw_record_id=1;")]
    [InlineData("INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at) SELECT 2,upper(trace_id),upper(span_id),parent_span_id,1,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at FROM monitor_spans WHERE raw_record_id=1;")]
    public async Task ProductionCoordinatorRejectsOtelToolNormalizedOwnerDrift(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='otel';").Single();
            LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorRejectsAStaleOtelSemanticToolAfterExactEventTypeDrift()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='otel';
                """).Single();
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE session_events SET type='event'
                WHERE event_id='018f0000-0000-7000-8000-000000000011';
                UPDATE local_workspace_nodes SET kind='event',name_text='event'
                WHERE source_kind='session_event' AND source_identity='018f0000-0000-7000-8000-000000000011';
                UPDATE local_workspace_node_source_references
                SET revision_input='otel-exact|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb|event|2026-08-26T09:00:00.0000000+00:00'
                WHERE source_kind='session_event' AND source_identity='018f0000-0000-7000-8000-000000000011';
                """);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() =>
            CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorUsesOnlyMcpToolNameForMcpIdentity()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE monitor_spans SET tool_name='GenericTool',mcp_tool_name=NULL WHERE raw_record_id=1;
                """);
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='otel';
                """).Single();
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal("recorded", tool.NameState);
        Assert.Equal("GenericTool", tool.NameText);
        Assert.Equal("not_observed", tool.ToolMetadata!.McpToolNameState);
        Assert.Null(tool.ToolMetadata.McpToolName);
    }

    [Fact]
    public async Task ProductionCoordinatorReadsAnUnresolvedOtelParentThroughTheExecutionUnknownGroup()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        string groupId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                "UPDATE monitor_spans SET parent_span_id='cccccccccccccccc' WHERE raw_record_id=1;");
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='otel';
                """).Single();
            groupId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_nodes
                WHERE source_kind='unknown_relation_group' AND source_identity='018f0000-0000-7000-8000-000000000010';
                """).Single();
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal(groupId, tool.ParentNodeId);
        Assert.Equal("unknown", tool.RelationshipAuthority);
        Assert.Contains(detail.Detail.Nodes, value => value.NodeId == groupId && value.Kind == "unknown_relation_group");
    }

    [Fact]
    public async Task UnlinkedSameTraceOtelRowsDoNotExpandTheCanonicalRevisionInput()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        var service = CreateRoundFiveService(temp.DatabasePath);
        var before = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        using (var connection = OpenFile(temp.DatabasePath))
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
                VALUES(2,'raw-otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-26T00:00:07.0000000+00:00','{}','{}',1,randomblob(32));
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,status,start_time,end_time,projected_at)
                VALUES(2,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','cccccccccccccccc',NULL,0,'execute_tool','tool_call','Unlinked','Unlinked','ok','2026-08-26T00:00:07.0000000+00:00','2026-08-26T00:00:08.0000000+00:00','2026-08-26T00:00:08.0000000+00:00');
                INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens)
                VALUES(2,0,1,10);
                """);

        var after = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        Assert.Equal(before.WorkspaceRevision, after.WorkspaceRevision);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_sessions SET label_state='future_label_state';")]
    [InlineData("UPDATE local_workspace_sessions SET label_state='recorded',label_text=NULL,label_source_identity=NULL,label_expires_at=NULL;")]
    [InlineData("UPDATE local_workspace_sessions SET timing_state='future_timing_state';")]
    [InlineData("UPDATE local_workspace_sessions SET status='completed',timing_state='recorded',started_at=NULL,ended_at='2026-08-26T00:00:02.0000000+00:00',last_seen_at='2026-08-26T00:00:02.0000000+00:00',last_seen_epoch_ms=1787702402000,duration_ms=1000;")]
    [InlineData("UPDATE local_workspace_sessions SET status='active',timing_state='recorded',started_at='2026-08-26T00:00:01.0000000+00:00',ended_at='2026-08-26T00:00:02.0000000+00:00',last_seen_at='2026-08-26T00:00:02.0000000+00:00',last_seen_epoch_ms=1787702402000,duration_ms=1000;")]
    [InlineData("UPDATE local_workspace_sessions SET status='completed',timing_state='recorded',started_at='2026-08-26T00:00:01.0000000+00:00',ended_at='2026-08-26T00:00:02.0000000+00:00',last_seen_at='2026-08-26T00:00:02.0000000+00:00',last_seen_epoch_ms=1787702402000,duration_ms=NULL;")]
    [InlineData("UPDATE local_workspace_session_activity SET state='recorded',count=NULL WHERE kind='tool';")]
    [InlineData("UPDATE local_workspace_token_observations SET input_tokens=-1;")]
    public async Task ProductionCoordinatorRejectsATamperedClosedSessionSummaryRow(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                "PRAGMA ignore_check_constraints=ON;" + mutation);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None));

        Assert.Equal("local_monitor_ui_unavailable", error.Message);
    }

    [Theory]
    [InlineData("DELETE FROM local_workspace_node_source_references WHERE event_id='018f0000-0000-7000-8000-000000000031';", "tool")]
    [InlineData("UPDATE local_workspace_tool_metadata SET started_state='not_observed' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='session_sdk');", "tool")]
    [InlineData("UPDATE local_workspace_nodes SET tool_activity_count=999 WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='session_sdk');", "tool")]
    [InlineData("UPDATE local_workspace_nodes SET token_authority='session_run',token_state='recorded',available_execution_count=1,input_token_state='recorded',input_tokens=999 WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='session_sdk');", "tool")]
    [InlineData("UPDATE session_events SET trace_id='cccccccccccccccccccccccccccccccc' WHERE event_id='018f0000-0000-7000-8000-000000000031';", "tool")]
    [InlineData("UPDATE local_workspace_semantic_receipts SET scope_kind='native_session' WHERE semantic_kind='tool' AND source_family='session_sdk';", "tool")]
    [InlineData("UPDATE local_workspace_subagent_lifecycle SET started_state='not_observed';", "subagent")]
    [InlineData("UPDATE local_workspace_nodes SET subagent_activity_count=999 WHERE source_kind='semantic_subagent';", "subagent")]
    [InlineData("UPDATE local_workspace_nodes SET token_authority='session_run',token_state='recorded',available_execution_count=1,total_token_state='recorded',total_tokens=999 WHERE source_kind='semantic_subagent';", "subagent")]
    [InlineData("INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('018f0000-0000-7000-8000-000000000023','018f0000-0000-7000-8000-000000000001','018f0000-0000-7000-8000-000000000020','copilot-sdk','copilot-sdk-stream','sdk-subagent-failed','subagent.failed','2026-08-26T00:00:05.5000000+00:00','not_captured');", "subagent")]
    public async Task ProductionCoordinatorRejectsSdkSemanticOwnerGraphDrift(string mutation, string semanticKind)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                semanticKind == "tool"
                    ? "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='session_sdk';"
                    : "SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='subagent' AND source_family='session_sdk';").Single();
            LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input='synthetic|forged|event|2026-08-26T00:00:05.2500000+00:00' WHERE event_id='018f0000-0000-7000-8000-000000000025';")]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input='session_run|018f0000-0000-7000-8000-000000000020|started|active|' WHERE source_kind='session_run' AND source_identity='018f0000-0000-7000-8000-000000000020';")]
    [InlineData("UPDATE local_workspace_nodes SET execution_id=(SELECT execution_id FROM local_workspace_execution_headers WHERE source_identity='018f0000-0000-7000-8000-000000000010') WHERE source_kind='session_event' AND source_identity='018f0000-0000-7000-8000-000000000025';")]
    [InlineData("UPDATE local_workspace_nodes SET parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE source_kind='execution_root' AND source_identity='018f0000-0000-7000-8000-000000000010') WHERE source_kind='session_event' AND source_identity='018f0000-0000-7000-8000-000000000025';")]
    [InlineData("UPDATE session_events SET parent_event_id='018f0000-0000-7000-8000-000000000011' WHERE event_id='018f0000-0000-7000-8000-000000000025';")]
    [InlineData("UPDATE session_events SET run_id='018f0000-0000-7000-8000-000000000010' WHERE event_id='018f0000-0000-7000-8000-000000000025';")]
    [InlineData("UPDATE local_workspace_execution_headers SET tool_activity_state='recorded',tool_activity_count=999; UPDATE local_workspace_nodes SET tool_activity_state='recorded',tool_activity_count=999 WHERE source_kind='execution_root';")]
    [InlineData("UPDATE local_workspace_execution_headers SET retry_activity_state='recorded',retry_activity_count=999; UPDATE local_workspace_nodes SET retry_activity_state='recorded',retry_activity_count=999 WHERE source_kind='execution_root';")]
    [InlineData("UPDATE local_workspace_execution_headers SET input_token_state='recorded',input_tokens=999; UPDATE local_workspace_nodes SET input_token_state='recorded',input_tokens=999 WHERE source_kind='execution_root';")]
    public async Task ProductionCoordinatorRejectsExecutionOrRawEventOwnerGraphDrift(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
            LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("orphan_event")]
    [InlineData("extra_event_parent")]
    [InlineData("extra_unknown_group")]
    [InlineData("receiptless_tool")]
    [InlineData("receiptless_subagent")]
    public async Task ProductionCoordinatorRejectsUnownedWorkspaceGraphRows(string corruption)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            switch (corruption)
            {
                case "orphan_event":
                    LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                        PRAGMA foreign_keys=OFF;
                        INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
                        VALUES('orphan-event','018f0000-0000-7000-8000-000000000001','missing-run','copilot-sdk','synthetic','orphan-source','event','2026-08-26T00:00:07.0000000+00:00','not_captured');
                        """);
                    break;
                case "extra_event_parent":
                    LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                        INSERT INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
                        SELECT child.node_id,parent.node_id,'parent','exact',child.source_ordinal
                        FROM local_workspace_nodes child,local_workspace_nodes parent
                        WHERE child.source_kind='session_event' AND child.source_identity='018f0000-0000-7000-8000-000000000025'
                          AND parent.source_kind='session_event' AND parent.source_identity='018f0000-0000-7000-8000-000000000021';
                        """);
                    break;
                case "extra_unknown_group":
                    InsertUnownedWorkspaceNode(connection, "unknown_relation_group", "forged-unknown-group");
                    break;
                case "receiptless_tool":
                    InsertReceiptlessSemanticNode(connection, "semantic_tool", "tool");
                    break;
                case "receiptless_subagent":
                    InsertReceiptlessSemanticNode(connection, "semantic_subagent", "subagent");
                    break;
            }
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("raw_event")]
    [InlineData("token_observation")]
    [InlineData("monitor_span")]
    public async Task ProductionCoordinatorBoundsLiveSourceOwnersBeforeGraphProof(string sourceKind)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            var insertion = sourceKind switch
            {
                "raw_event" => """
                    PRAGMA foreign_keys=OFF;
                    WITH RECURSIVE sequence(value) AS (VALUES(0) UNION ALL SELECT value+1 FROM sequence WHERE value<4096)
                    INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
                    SELECT printf('unprojected-%04d',value),'018f0000-0000-7000-8000-000000000001','missing-run','copilot-sdk','synthetic',
                           printf('unprojected-source-%04d',value),'event','2026-08-26T00:00:07.0000000+00:00','not_captured'
                    FROM sequence;
                    """,
                "token_observation" => """
                    WITH RECURSIVE sequence(value) AS (VALUES(0) UNION ALL SELECT value+1 FROM sequence WHERE value<4096)
                    INSERT INTO local_workspace_token_observations(
                      session_id,execution_id,authority,authority_rank,source_identity,input_tokens)
                    SELECT '018f0000-0000-7000-8000-000000000001','018f0000-0000-7000-8000-000000000020',
                           'session_run',0,printf('unprojected-token-%04d',value),1
                    FROM sequence;
                    """,
                "monitor_span" => """
                    WITH RECURSIVE sequence(value) AS (VALUES(0) UNION ALL SELECT value+1 FROM sequence WHERE value<4096)
                    INSERT INTO monitor_spans(
                      raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,
                      mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at)
                    SELECT 10000+value,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',NULL,0,
                           'execute_tool','tool_call','Read','ReadMcp',
                           'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ok',2000,
                           '2026-08-26T00:00:02.0000000+00:00','2026-08-26T00:00:04.0000000+00:00',
                           '2026-08-26T00:00:04.0000000+00:00'
                    FROM sequence;
                    """,
                _ => throw new InvalidOperationException(sourceKind),
            };
            LocalWorkspaceProjectionSchemaTests.Execute(connection, insertion);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());
        Assert.Equal("workspace_too_large", error.Error);
    }

    [Fact]
    public async Task NodeReadCarriesGlobalLatestAndClosedV5NodeFactsWithoutSerializerInference()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00');
            INSERT INTO session_runs(run_id,session_id,source_surface,started_at,status) VALUES
              ('run-old','018f0000-0000-7000-8000-000000000001','copilot-sdk','2026-08-26T00:00:00.0000000+00:00','completed'),
              ('run-latest','018f0000-0000-7000-8000-000000000001','copilot-sdk','2026-08-26T00:01:00.0000000+00:00','active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-child','018f0000-0000-7000-8000-000000000001','run-old','copilot-sdk','synthetic','source-child','event','2026-08-26T00:00:01.0000000+00:00','not_captured'),
              ('event-grandchild','018f0000-0000-7000-8000-000000000001','run-old','copilot-sdk','synthetic','source-grandchild','event','2026-08-26T00:00:02.0000000+00:00','not_captured'),
              ('event-sibling','018f0000-0000-7000-8000-000000000001','run-old','copilot-sdk','synthetic','source-sibling','event','2026-08-26T00:00:03.0000000+00:00','not_captured');
            UPDATE session_events SET parent_event_id='event-child' WHERE event_id='event-grandchild';
            """);
        InstallSkillAuthorities(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var oldRoot = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='execution_root' AND source_identity='run-old';").Single();
        using var transaction = connection.BeginTransaction();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var detail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: oldRoot), CancellationToken.None);

        var execution = Assert.Single(detail.Executions);
        Assert.False(Assert.IsType<bool>(execution.GetType().GetProperty("Latest")?.GetValue(execution)));
        var summary = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Summary, SessionId), CancellationToken.None);
        Assert.Single(summary.Executions, static value => value.Latest);
        Assert.Equal("run-latest", Assert.Single(summary.Executions, static value => value.Latest).SourceIdentity);
        Assert.False(Assert.Single(summary.Executions, static value => value.SourceIdentity == "run-old").Latest);
        var root = Assert.Single(detail.Nodes, node => node.NodeId == oldRoot);
        Assert.False(Assert.IsType<bool>(root.GetType().GetProperty("HasMoreChildren")?.GetValue(root)));
        var collapsed = root.GetType().GetProperty("CollapsedChildren")?.GetValue(root);
        Assert.NotNull(collapsed);
        Assert.Equal("complete", collapsed.GetType().GetProperty("State")?.GetValue(collapsed));
        Assert.Equal(0L, collapsed.GetType().GetProperty("Value")?.GetValue(collapsed));
        var child = Assert.Single(detail.Nodes, node => node.SourceIdentity == "event-child");
        Assert.True(Assert.IsType<bool>(child.GetType().GetProperty("HasMoreChildren")?.GetValue(child)));
        var references = Assert.IsAssignableFrom<System.Collections.IEnumerable>(root.GetType().GetProperty("SourceReferences")?.GetValue(root));
        Assert.Single(references.Cast<object>());
        Assert.Equal("session_run", references.Cast<object>().Single().GetType().GetProperty("SourceKind")?.GetValue(references.Cast<object>().Single()));

        var completeTimeline = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Timeline, SessionId, ExecutionId: execution.ExecutionId, Limit: 10), CancellationToken.None);
        var completeRoot = Assert.Single(completeTimeline.Nodes, node => node.NodeId == oldRoot);
        Assert.False(completeRoot.HasMoreChildren);
        var pagedTimeline = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Timeline, SessionId, ExecutionId: execution.ExecutionId, Limit: 1), CancellationToken.None);
        var pagedRoot = Assert.Single(pagedTimeline.Nodes, node => node.NodeId == oldRoot);
        Assert.True(pagedRoot.HasMoreChildren);
    }

    [Fact]
    public async Task PermissionRequestNodeCarriesClosedNonRecordedDecisionAndWaitFactsIntoRevision()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-permission','018f0000-0000-7000-8000-000000000001','claude-code',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('permission-event','018f0000-0000-7000-8000-000000000001','run-permission','claude-code','claude-code-hook','permission-source','PermissionRequest','2026-08-26T00:00:01.0000000+00:00','not_captured');
            """);
        InstallSkillAuthorities(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
        var nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_identity='permission-event';").Single();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load(),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:00:00Z")));

        LocalWorkspaceSessionDetailContribution permission;
        using (var transaction = connection.BeginTransaction(deferred: true))
            permission = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: nodeId), CancellationToken.None);

        var node = Assert.Single(permission.Nodes, value => value.NodeId == nodeId);
        Assert.Equal("permission", node.Kind);
        var metadata = node.GetType().GetProperty("PermissionMetadata")?.GetValue(node);
        Assert.NotNull(metadata);
        Assert.Equal("not_observed", metadata.GetType().GetProperty("DecisionState")?.GetValue(metadata));
        Assert.Null(metadata.GetType().GetProperty("Decision")?.GetValue(metadata));
        Assert.Equal("not_observed", metadata.GetType().GetProperty("WaitState")?.GetValue(metadata));
        Assert.Null(metadata.GetType().GetProperty("WaitMilliseconds")?.GetValue(metadata));
        var source = Assert.Single(node.SourceReferences!);
        Assert.Equal("permission-event", source.EventId);

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE session_events SET source_event_id='permission-source-2' WHERE event_id='permission-event';");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-26T00:00:01Z"));
        using var changedTransaction = connection.BeginTransaction(deferred: true);
        var changed = await contributor.ReadAsync(new DirectReadTransaction(connection, changedTransaction),
            new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: nodeId), CancellationToken.None);
        Assert.NotEqual(permission.CanonicalRevisionInput, changed.CanonicalRevisionInput);
        Assert.NotNull(Assert.Single(changed.Nodes, value => value.NodeId == nodeId).PermissionMetadata);
    }

    [Fact]
    public async Task TimelineTopLevelHasMoreCountsUnknownRelationGroupInsideLimitPlusOnePage()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES
              ('run-parent','session-a','copilot-sdk','native-parent',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('run-child','session-a','copilot-sdk','native-child',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-parent','session-a','run-parent','copilot-sdk',NULL,'synthetic','source-parent','event','2026-08-26T00:00:00.0000000+00:00','not_captured'),
              ('event-direct','session-a','run-child','copilot-sdk',NULL,'synthetic','source-direct','event','2026-08-26T00:00:01.0000000+00:00','not_captured'),
              ('event-unknown','session-a','run-child','copilot-sdk','event-parent','synthetic','source-unknown','event','2026-08-26T00:00:02.0000000+00:00','not_captured');
            """);
        InstallSkillAuthorities(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
        var executionId = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT execution_id FROM local_workspace_execution_headers WHERE source_identity='run-child';").Single();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load(),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:00:00Z")));
        using var transaction = connection.BeginTransaction(deferred: true);

        var complete = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Timeline, "session-a", ExecutionId: executionId, Limit: 2), CancellationToken.None);
        var completeRoot = Assert.Single(complete.Nodes, node => node.SourceKind == "execution_root");
        Assert.False(completeRoot.HasMoreChildren);
        Assert.Contains(complete.Nodes, node => node.SourceKind == "unknown_relation_group");

        var paged = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Timeline, "session-a", ExecutionId: executionId, Limit: 1), CancellationToken.None);
        var pagedRoot = Assert.Single(paged.Nodes, node => node.SourceKind == "execution_root");
        Assert.True(pagedRoot.HasMoreChildren);
        Assert.Contains(paged.Nodes, node => node.SourceKind == "unknown_relation_group");
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Timeline")]
    [InlineData("Node")]
    public async Task DetailCommandsAndFullScanWorkAreIndependentOfUnrelatedSessionCardinality(string kindName)
    {
        var one = await ObserveDetailRead(kindName, 1);
        var tenThousand = await ObserveDetailRead(kindName, 10_000);

        Assert.Equal(one.Sql, tenThousand.Sql);
        Assert.Equal(one.FullScanSteps, tenThousand.FullScanSteps);
        Assert.NotEmpty(one.Sql);
        Assert.Equal(1, one.PublicationLeaseCount);
        Assert.Equal(1, one.ConnectionCount);
        Assert.Equal(1, one.ReadTransactionCount);
        Assert.Equal(one.PublicationLeaseCount, tenThousand.PublicationLeaseCount);
        Assert.Equal(one.ConnectionCount, tenThousand.ConnectionCount);
        Assert.Equal(one.ReadTransactionCount, tenThousand.ReadTransactionCount);
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Timeline")]
    [InlineData("Node")]
    public async Task SanitizedDetailReadsDoNotSelectRawCarrierValuesOrAcquireRetentionAccessLeases(string kindName)
    {
        var observed = await ObserveDetailRead(kindName, 1, inflateRawCarriers: true);

        Assert.DoesNotContain(observed.Sql, statement => statement.Contains("payload_json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observed.Sql, statement => statement.Contains("resource_attributes_json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observed.Sql, statement => statement.Contains("content_json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observed.Sql, statement => statement.Contains("SELECT *", StringComparison.OrdinalIgnoreCase)
            && (statement.Contains("raw_records", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("session_event_content", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(observed.Sql, statement =>
            statement.Contains("retention_leases", StringComparison.OrdinalIgnoreCase)
            && (statement.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SummaryReadRejectsTwoHundredFiftySevenPersistedExecutionsAfterProjectionSucceeds()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00');");
        for (var index = 0; index < 257; index++)
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $"INSERT INTO session_runs VALUES('run-{index:D4}','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        using var transaction = connection.BeginTransaction();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Summary, SessionId), CancellationToken.None));

        Assert.Equal("workspace_too_large", error.Error);
    }

    [Fact]
    public async Task DurableNodeOverflowMarkerRejectsEveryDetailReadAndClearsAfterSourceShrink()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-1','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown'); INSERT INTO session_runs VALUES('run-2','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown'); INSERT INTO session_events VALUES('event-zzzz','018f0000-0000-7000-8000-000000000001','run-2','copilot-sdk',NULL,NULL,NULL,'synthetic','source-parent','event','2026-08-26T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);");
        for (var index = 0; index < 4096; index++)
        {
            var parent = index == 0 ? "'event-zzzz'" : "NULL";
            var relationship = index == 0 ? "'explicit_link'" : "NULL";
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $"INSERT INTO session_events VALUES('event-{index:D4}','018f0000-0000-7000-8000-000000000001','run-1','copilot-sdk',{parent},NULL,NULL,'synthetic','source-{index:D4}','event','2026-08-26T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,{relationship},NULL,NULL);");
        }
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var rootId = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='execution_root' AND source_identity='run-1';").Single();
        Assert.Equal("1", LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(node_overflow AS TEXT) FROM local_workspace_sessions;").Single());
        var retainedCount = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_nodes;").Single();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal(retainedCount, LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_nodes;").Single());
        Assert.Equal("1", LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(node_overflow AS TEXT) FROM local_workspace_sessions;").Single());

        foreach (var request in new[]
        {
            new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Summary, SessionId),
            new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Timeline, SessionId),
            new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: rootId),
            new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Content, SessionId, NodeId: rootId, ContentPart: "event_content"),
        })
        {
            using var transaction = connection.BeginTransaction();
            var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: FixedSkillRegistryGenerationAuthority.Load());
            var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
                await contributor.ReadAsync(new DirectReadTransaction(connection, transaction), request, CancellationToken.None));
            Assert.Equal("workspace_too_large", error.Error);
        }

        using (var restored = new SqliteConnection("Data Source=:memory:"))
        {
            restored.Open();
            connection.BackupDatabase(restored);
            LocalWorkspaceProjectionSchemaV1.Validate(restored, null);
            Assert.Equal("1", LocalWorkspaceProjectionSchemaTests.Strings(restored, "SELECT CAST(node_overflow AS TEXT) FROM local_workspace_sessions;").Single());
        }

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "DELETE FROM session_events WHERE event_id NOT IN ('event-0000','event-zzzz');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal("0", LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(node_overflow AS TEXT) FROM local_workspace_sessions;").Single());
        Assert.Equal(["event-0000", "event-zzzz"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT source_identity FROM local_workspace_nodes WHERE source_kind='session_event' ORDER BY source_identity;"));
    }

    [Fact]
    public async Task NodeReadRejectsAnAncestryCycleAsUnavailableInsteadOfTooLarge()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-1','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var rootId = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT node_id FROM local_workspace_nodes;").Single();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_workspace_nodes SET parent_node_id=node_id WHERE node_id=$node_id;";
            command.Parameters.AddWithValue("$node_id", rootId);
            command.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: rootId), CancellationToken.None));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public void RecordedTimelineCursorPermitsTheFullSignedTickDomainIncludingZero()
    {
        var key = new byte[32];
        var filter = new LocalMonitorV1TimelineFilter(SessionId, new string('1', 64), ExecutionId, null, 100);
        foreach (var ticks in new[] { long.MinValue, 0L, long.MaxValue })
        {
            var expected = new LocalMonitorV1TimelinePosition(0, ticks, 0, "node-00000000000000000000000000000001");
            var cursor = LocalMonitorV1TimelineCursor.Encode(key, filter, expected);
            Assert.True(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TimelineExposesUnknownGroupAndRejectsAValidCursorForAnAbsentSibling()
    {
        var snapshot = Snapshot();
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        var bytes = LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 100, null, key);
        using var json = JsonDocument.Parse(bytes);
        var items = json.RootElement.GetProperty("items");
        Assert.Single(items.EnumerateArray());
        Assert.Equal("unknown_relation_group", items[0].GetProperty("kind").GetString());
        Assert.Equal(1, items[0].GetProperty("child_count").GetInt32());

        var filter = new LocalMonitorV1TimelineFilter(SessionId, snapshot.WorkspaceRevision, ExecutionId, null, 100);
        var absent = LocalMonitorV1TimelineCursor.Encode(key, filter,
            new LocalMonitorV1TimelinePosition(1, 0, 99, "node-ffffffffffffffffffffffffffffffff"));
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 100, absent, key));
        Assert.Equal("invalid_cursor", error.Error);
    }

    [Fact]
    public void SummaryUsesExactNativeSessionAndInstructionContentBindings()
    {
        var bytes = LocalMonitorV1SessionDetailApplication.SerializeSummary(Snapshot());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"native_session_ids\":[\"native-session\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"native_session_ids\":[\"run-1\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"instruction\":{\"state\":\"recorded\",\"label\":\"instruction\",\"additional_count\":0,\"content_available\":true}", text, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"copilot-sdk\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryTimelineAndNodeUsePersistedExactChildCountsWhenChildrenAreNotLoaded()
    {
        var snapshot = Snapshot();
        var root = snapshot.Detail.Nodes.Single(static node => node.SourceKind == "execution_root");
        var group = snapshot.Detail.Nodes.Single(static node => node.Kind == "unknown_relation_group");
        var exact = snapshot with
        {
            Detail = snapshot.Detail with
            {
                Executions = [snapshot.Detail.Executions.Single() with { ChildCount = 301 }],
                Nodes = [root with { ChildCount = 301 }, group with { ChildCount = 300 }],
            },
        };

        using var summary = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeSummary(exact));
        using var timeline = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
            exact, ExecutionId, null, 100, null, new byte[32]));
        using var node = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeNode(exact, group.NodeId));

        Assert.Equal(301, summary.RootElement.GetProperty("executions")[0].GetProperty("child_count").GetInt32());
        Assert.Equal(300, timeline.RootElement.GetProperty("items")[0].GetProperty("child_count").GetInt32());
        Assert.Equal(300, node.RootElement.GetProperty("node").GetProperty("child_count").GetInt32());
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot()
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 0, 0, "recorded", "instruction", "completed", "full",
            new("recorded", ["copilot-sdk"]), new("recorded", ["model"]), activity, tokens,
            "recorded", "2026-08-26T00:00:00.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", 1000, [], "revision");
        var scope = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
            LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null);
        var execution = new LocalWorkspaceExecutionDetail(ExecutionId, SessionId, "session_run", "run-1", 0, "completed", "completed", "model", null,
            "recorded", 638917344000000000, 638917344010000000, 1000, activity, tokens, "copilot-sdk", "1.0", 1);
        var root = Node("node-00000000000000000000000000000001", "execution_root", "execution", null, 0, activity, tokens);
        var group = Node("node-00000000000000000000000000000002", "unknown_relation_group", "unknown_relation_group", null, 1, activity, tokens) with { ChildCount = 1 };
        var child = Node("node-00000000000000000000000000000003", "session_event", "event", group.NodeId, 2, activity, tokens);
        var detail = new LocalWorkspaceSessionDetailContribution([execution], [root, group, child], [],
            [new(root.NodeId, "instruction", "available", "instruction-event", "revision")], ["native-session"], ["1.0"], "instruction-event", 0, "canonical");
        return new(scope, detail, new string('1', 64));
    }

    private static LocalWorkspaceNodeDetail Node(string id, string sourceKind, string kind, string? parent, long ordinal,
        LocalWorkspaceActivityFacts activity, LocalWorkspaceTokenFacts tokens) =>
        new(id, SessionId, ExecutionId, sourceKind, sourceKind == "execution_root" ? "run-1" : id, ordinal, parent,
            parent is null && kind == "unknown_relation_group" ? "unknown" : "exact", kind, "not_observed", null,
            "completed", "completed", "missing", null, null, null, activity, tokens, null, null, null);

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string ExecutionId = "018f0000-0000-7000-8000-000000000003";

    private static void CloneUnrelatedNodes(SqliteConnection connection, int count)
    {
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(local_workspace_nodes);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        var expressions = columns.Select(static column => column switch
        {
            "node_id" => "$node_id",
            "source_kind" => "'session_event'",
            "source_identity" => "$source_identity",
            "source_ordinal" => "$source_ordinal",
            "parent_node_id" => "NULL",
            "relationship_authority" => "'unknown'",
            "kind" => "'event'",
            _ => column,
        });
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO local_workspace_nodes({string.Join(',', columns)}) SELECT {string.Join(',', expressions)} FROM local_workspace_nodes WHERE source_kind='execution_root';";
        var nodeId = command.Parameters.Add("$node_id", Microsoft.Data.Sqlite.SqliteType.Text);
        var sourceIdentity = command.Parameters.Add("$source_identity", Microsoft.Data.Sqlite.SqliteType.Text);
        var sourceOrdinal = command.Parameters.Add("$source_ordinal", Microsoft.Data.Sqlite.SqliteType.Integer);
        for (var index = 0; index < count; index++)
        {
            var identity = $"unrelated-{index:D4}";
            nodeId.Value = LocalWorkspaceProjectionStore.StableNodeId("session_event", identity);
            sourceIdentity.Value = identity;
            sourceOrdinal.Value = index + 1;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static async Task<ObservedRead> ObserveDetailRead(string kindName, int sessionCount, bool inflateRawCarriers = false)
    {
        using var temp = new MonitorTempDirectory();
        var targetSession = AlertCenterRouteTests.SeedPersistedTraceAndSession(
            temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        using (var ownerConnection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            ownerConnection.Open();
            LocalWorkspaceProjectionSchemaTests.Execute(ownerConnection,
                "UPDATE monitor_spans SET span_id=printf('%016x',span_ordinal+1) WHERE trace_id='00000000000000000000000000000001';");
        }
        var sessionStore = new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(
            temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionOtelEnricher(
            temp.DatabasePath, sessionStore, temp.RetentionContext, temp.TimeProvider).ProcessNextBatch(100);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartSessionOtelEnrichment = false,
        });
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        var sessionColumns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(sessions);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) sessionColumns.Add(reader.GetString(1));
        }
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO sessions({string.Join(',', sessionColumns)}) SELECT $id,{string.Join(',', sessionColumns.Skip(1))} FROM sessions WHERE session_id=$target;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            command.Parameters.AddWithValue("$target", targetSession.ToString("D"));
            for (var index = 1; index < sessionCount; index++)
            {
                id.Value = $"01990000-0000-7000-8000-{index:D12}";
                command.ExecuteNonQuery();
            }
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, DateTimeOffset.UnixEpoch,
                FixedSkillRegistryGenerationAuthority.Load());
            if (inflateRawCarriers)
            {
                command.CommandText = "UPDATE raw_records SET payload_json=$large,resource_attributes_json=$large WHERE id IN (SELECT raw_record_id FROM monitor_spans WHERE trace_id='00000000000000000000000000000001'); UPDATE session_event_content SET content_json=$large WHERE event_id IN (SELECT event_id FROM session_events WHERE session_id=$target);";
                command.Parameters.AddWithValue("$large", new string('x', 1_048_577));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        using var rootCommand = connection.CreateCommand();
        rootCommand.CommandText = "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND source_kind='execution_root';";
        rootCommand.Parameters.AddWithValue("$session", targetSession.ToString("D"));
        var rootId = (string)rootCommand.ExecuteScalar()!;
        connection.Close();
        var kind = Enum.Parse<LocalRepositorySessionDetailRequestKind>(kindName);
        var request = new LocalRepositorySessionDetailRequest(kind, targetSession.ToString("D"),
            NodeId: kind == LocalRepositorySessionDetailRequestKind.Node ? rootId : null);
        var gate = new CountingPublicationGate();
        var connectionCount = 0;
        NativeDetailObserver? observer = null;
        IReadOnlyList<string>? sql = null;
        IReadOnlyList<int>? fullScanSteps = null;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            connectionOpenedObserver: opened => { connectionCount++; observer = new NativeDetailObserver(opened); },
            finalReturnObserver: () =>
            {
                sql = observer!.Sql.ToArray();
                fullScanSteps = observer.FullScanSteps.ToArray();
                observer.Dispose();
            },
            publicationGate: gate,
            skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.Load());
        await service.ReadDetailAsync(request, CancellationToken.None);
        Assert.NotNull(sql);
        Assert.NotNull(fullScanSteps);
        return new(sql, fullScanSteps, gate.ReadCount, connectionCount,
            sql.Count(statement => statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record ObservedRead(
        IReadOnlyList<string> Sql,
        IReadOnlyList<int> FullScanSteps,
        int PublicationLeaseCount,
        int ConnectionCount,
        int ReadTransactionCount);

    private static void InitializeRoundFiveSemanticFixture(
        string databasePath,
        string sessionId,
        string otelRunId,
        string sdkRunId)
    {
        using (var connection = OpenFile(databasePath))
        using (var transaction = connection.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            transaction.Commit();
        }
        new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(databasePath).CreateSchema();
        using (var connection = OpenFile(databasePath))
        using (var transaction = connection.BeginTransaction())
        {
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot.SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }
        using (var connection = OpenFile(databasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                INSERT INTO sessions VALUES('{{sessionId}}','completed','full',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:06.0000000+00:00');
                INSERT INTO session_native_ids VALUES('{{sessionId}}','copilot-sdk','native-session-sdk','native','2026-08-26T00:00:00.0000000+00:00');
                INSERT INTO session_runs VALUES
                  ('{{otelRunId}}','{{sessionId}}','claude-code','native-run-otel','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:04.0000000+00:00',NULL,NULL,NULL,'completed'),
                  ('{{sdkRunId}}','{{sessionId}}','copilot-sdk','native-child-sdk',NULL,NULL,NULL,'2026-08-26T00:00:05.0000000+00:00','2026-08-26T00:00:06.0000000+00:00',NULL,NULL,NULL,'completed');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
                  ('018f0000-0000-7000-8000-000000000011','{{sessionId}}','{{otelRunId}}','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-26T09:00:00.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000021','{{sessionId}}','{{sdkRunId}}','copilot-sdk',NULL,'copilot-sdk-stream','sdk-subagent-started','subagent.started','2026-08-26T00:00:05.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000022','{{sessionId}}','{{sdkRunId}}','copilot-sdk',NULL,'copilot-sdk-stream','sdk-subagent-completed','subagent.completed','2026-08-26T00:00:06.0000000+00:00','not_captured');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,parent_event_id,type,occurred_at,content_state) VALUES
                  ('018f0000-0000-7000-8000-000000000031','{{sessionId}}','{{sdkRunId}}','copilot-sdk','copilot-sdk-stream','sdk-tool-start',NULL,'tool.execution_start','2026-08-26T00:00:03.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000032','{{sessionId}}','{{sdkRunId}}','copilot-sdk','copilot-sdk-stream','sdk-tool-complete','018f0000-0000-7000-8000-000000000031','tool.execution_complete','2026-08-26T00:00:04.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000025','{{sessionId}}','{{sdkRunId}}','copilot-sdk','synthetic','generic-event',NULL,'event','2026-08-26T00:00:05.2500000+00:00','not_captured');
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at)
                  VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',NULL,0,'execute_tool','tool_call','Read','ReadMcp','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ok',2000,
                    '2026-08-26T00:00:02.0000000+00:00','2026-08-26T00:00:04.0000000+00:00','2026-08-26T00:00:04.0000000+00:00');
                """);
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        }
    }

    private static void InsertUnownedWorkspaceNode(SqliteConnection connection, string sourceKind, string sourceIdentity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_workspace_nodes(
              node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,
              relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
            SELECT $node,header.session_id,header.execution_id,$source_kind,$source_identity,999,NULL,
                   'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
            FROM local_workspace_execution_headers header
            WHERE header.source_identity='018f0000-0000-7000-8000-000000000020';
            """;
        command.Parameters.AddWithValue("$node", LocalWorkspaceProjectionStore.StableNodeId(sourceKind, sourceIdentity));
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$source_identity", sourceIdentity);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void InsertReceiptlessSemanticNode(SqliteConnection connection, string sourceKind, string semanticKind)
    {
        var sourceIdentity = "unowned-" + semanticKind;
        var nodeId = LocalWorkspaceProjectionStore.StableNodeId(sourceKind, sourceIdentity);
        var columns = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT name FROM pragma_table_info('local_workspace_nodes') ORDER BY cid;");
        static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
        var columnList = string.Join(',', columns.Select(Quote));
        var projection = string.Join(',', columns.Select(column => column switch
        {
            "node_id" => "$node",
            "source_identity" => "$source_identity",
            "source_ordinal" => "source_ordinal+1000",
            _ => Quote(column),
        }));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                INSERT INTO local_workspace_nodes({columnList})
                SELECT {projection} FROM local_workspace_nodes WHERE source_kind=$source_kind LIMIT 1;
                """;
            command.Parameters.AddWithValue("$node", nodeId);
            command.Parameters.AddWithValue("$source_identity", sourceIdentity);
            command.Parameters.AddWithValue("$source_kind", sourceKind);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var metadataTable = semanticKind == "tool" ? "local_workspace_tool_metadata" : "local_workspace_subagent_lifecycle";
        var metadataColumns = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            $"SELECT name FROM pragma_table_info('{metadataTable}') ORDER BY cid;");
        var metadataColumnList = string.Join(',', metadataColumns.Select(Quote));
        var metadataProjection = string.Join(',', metadataColumns.Select(column => column == "node_id" ? "$node" : Quote(column)));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                INSERT INTO {metadataTable}({metadataColumnList})
                SELECT {metadataProjection} FROM {metadataTable}
                WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind=$source_kind AND node_id<>$node) LIMIT 1;
                INSERT INTO local_workspace_node_source_references
                SELECT $node,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input
                FROM local_workspace_node_source_references
                WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind=$source_kind AND node_id<>$node) LIMIT 1;
                INSERT INTO local_workspace_node_edges
                SELECT $node,related_node_id,relation_kind,relationship_authority,source_ordinal
                FROM local_workspace_node_edges
                WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind=$source_kind AND node_id<>$node) LIMIT 1;
                """;
            command.Parameters.AddWithValue("$node", nodeId);
            command.Parameters.AddWithValue("$source_kind", sourceKind);
            Assert.Equal(3, command.ExecuteNonQuery());
        }
    }

    private static void InstallSkillAuthorities(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
        CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
        SkillProjectionSchemaV1.Ensure(connection, transaction);
        transaction.Commit();
    }

    private static SqliteLocalRepositoryScopeSnapshotService CreateRoundFiveService(string databasePath)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        return new(databasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);
    }

    private static SqliteConnection OpenFile(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class CountingPublicationGate : ILocalWorkspacePublicationGate
    {
        internal int ReadCount { get; private set; }
        public ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }
        public ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class NativeDetailObserver : IDisposable
    {
        private const uint Statement = 0x01;
        private const uint Profile = 0x02;
        private const int FullScanStep = 1;
        private readonly IntPtr database;
        private readonly TraceCallback callback;
        private readonly HashSet<IntPtr> topLevel = [];
        internal List<string> Sql { get; } = [];
        internal List<int> FullScanSteps { get; } = [];

        internal NativeDetailObserver(SqliteConnection connection)
        {
            var handle = connection.Handle ?? throw new InvalidOperationException("SQLite handle unavailable.");
            database = handle.DangerousGetHandle();
            callback = Observe;
            Assert.Equal(0, sqlite3_trace_v2(database, Statement | Profile, callback, IntPtr.Zero));
        }

        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr detail)
        {
            if (kind == Statement)
            {
                var sql = Marshal.PtrToStringUTF8(detail) ?? string.Empty;
                if (!sql.TrimStart().StartsWith("-- ", StringComparison.Ordinal))
                {
                    topLevel.Add(statement);
                    Sql.Add(string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                }
            }
            else if (kind == Profile && topLevel.Remove(statement))
                FullScanSteps.Add(sqlite3_stmt_status(statement, FullScanStep, 0));
            return 0;
        }

        public void Dispose()
        {
            sqlite3_trace_v2(database, 0, null, IntPtr.Zero);
            GC.KeepAlive(callback);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TraceCallback(uint kind, IntPtr context, IntPtr statement, IntPtr detail);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_trace_v2(IntPtr database, uint mask, TraceCallback? callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int resetFlag);
    }

    private sealed class DirectReadTransaction(SqliteConnection connection, SqliteTransaction transaction) : ILocalRepositoryReadTransaction
    {
        public ValueTask<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken) => read(connection, transaction, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalWorkspaceSessionDetailSnapshotTests
{
    [Fact]
    public async Task DetailRegistryPinReleasesLeaseWhenCanonicalIdentityThrows()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        var authority = new ThrowingCanonicalRegistryAuthority();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority);
        using var connection = OpenFile(temp.DatabasePath);
        using var transaction = connection.BeginTransaction();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => contributor.ReadAsync(
            new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());

        Assert.Equal("canonical_identity_failure", error.Message);
        Assert.Equal(1, authority.DisposedLeaseCount);
    }

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
    public async Task ProductionCoordinatorSamplesOneAcceptedAtForSessionAndDetail()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        var clock = new CountingTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority,
            timeProvider: clock);

        await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task CompletedExecutionWithMissingEndPublishesInvalidTimingWithoutPartialFacts()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", sdkRunId);
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                $"UPDATE session_runs SET ended_at=NULL WHERE run_id='{sdkRunId}';");
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        var execution = Assert.Single(detail.Detail.Executions, value => value.SourceIdentity == sdkRunId);
        Assert.Equal("completed", execution.Status);
        Assert.Equal("invalid", execution.TimeAuthority);
        Assert.Null(execution.StartUtcTicks);
        Assert.Null(execution.EndUtcTicks);
        Assert.Null(execution.DurationMilliseconds);
    }

    [Fact]
    public async Task ActiveExecutionRejectsAnEndedIntervalAndKeepsAnOpenIntervalRecorded()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", sdkRunId);
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                $"UPDATE session_runs SET status='active' WHERE run_id='{sdkRunId}';");
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }

        var service = CreateRoundFiveService(temp.DatabasePath);
        var ended = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        var invalid = Assert.Single(ended.Detail.Executions, value => value.SourceIdentity == sdkRunId);
        Assert.Equal("active", invalid.Status);
        Assert.Equal("invalid", invalid.TimeAuthority);
        Assert.Null(invalid.StartUtcTicks);
        Assert.Null(invalid.EndUtcTicks);
        Assert.Null(invalid.DurationMilliseconds);

        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                $"UPDATE session_runs SET ended_at=NULL WHERE run_id='{sdkRunId}';");
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:02Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }
        var open = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        var recorded = Assert.Single(open.Detail.Executions, value => value.SourceIdentity == sdkRunId);
        Assert.Equal("recorded", recorded.TimeAuthority);
        Assert.NotNull(recorded.StartUtcTicks);
        Assert.Null(recorded.EndUtcTicks);
        Assert.Null(recorded.DurationMilliseconds);
    }

    [Fact]
    public async Task CompletedOtelToolWithMissingEndPublishesInvalidTimingWithoutPartialFacts()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection,
                "UPDATE monitor_spans SET end_time=NULL,duration_ms=NULL WHERE trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';");
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='otel';
                """).Single();
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal("completed", tool.Status);
        Assert.Equal("invalid", tool.TimeAuthority);
        Assert.Null(tool.StartUtcTicks);
        Assert.Null(tool.EndUtcTicks);
        Assert.Null(tool.DurationMilliseconds);
    }

    [Theory]
    [InlineData("active", null, "recorded")]
    [InlineData("completed", null, "inconsistent")]
    public async Task SessionTimingUsesStatusAuthorizedEndpointCoupling(
        string status,
        string? endedAt,
        string expectedState)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            using (var mutation = connection.CreateCommand())
            {
                mutation.CommandText = "UPDATE sessions SET status=$status,started_at='2026-08-26T00:00:00.0000000+00:00',ended_at=$ended WHERE session_id=$session;";
                mutation.Parameters.AddWithValue("$status", status);
                mutation.Parameters.AddWithValue("$ended", (object?)endedAt ?? DBNull.Value);
                mutation.Parameters.AddWithValue("$session", sessionId);
                Assert.Equal(1, mutation.ExecuteNonQuery());
            }
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        var session = Assert.IsType<LocalWorkspaceProjectionRow>(detail.Session.Session);
        Assert.Equal(expectedState, session.TimingState);
        Assert.Equal(status == "active" ? "2026-08-26T00:00:00.0000000+00:00" : null,
            expectedState == "recorded" ? session.StartedAt : null);
        Assert.Null(session.EndedAt);
        Assert.Null(session.DurationMilliseconds);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa%")]
    public async Task RawOtelEventDoesNotPromoteNonCanonicalTechnicalIdentity(string traceId)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string eventId = "018f0000-0000-7000-8000-000000000011";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            using (var mutation = connection.CreateCommand())
            {
                mutation.CommandText = "UPDATE session_events SET trace_id=$trace WHERE event_id=$event;";
                mutation.Parameters.AddWithValue("$trace", traceId);
                mutation.Parameters.AddWithValue("$event", eventId);
                Assert.Equal(1, mutation.ExecuteNonQuery());
            }
            using var refresh = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
            nodeId = LocalWorkspaceProjectionStore.StableNodeId("session_event", eventId);
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var raw = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Null(raw.TraceId);
        Assert.Null(raw.SpanId);
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

    [Theory]
    [InlineData("tool")]
    [InlineData("subagent")]
    [InlineData("skill")]
    public async Task ProductionCoordinatorRejectsMetadataAttachedToTheWrongNodeKind(string metadataKind)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            var rawNode = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_nodes
                WHERE source_kind='session_event' AND kind='event' ORDER BY node_id LIMIT 1;
                """).Single();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$node", rawNode);
            command.CommandText = metadataKind switch
            {
                "tool" => "INSERT INTO local_workspace_tool_metadata SELECT $node,caller_state,caller_node_id,started_state,completed_state,failed_state,exit_state,exit_code,mcp_server_identity_state,mcp_server_identity,mcp_server_name_state,mcp_server_name,mcp_tool_name_state,mcp_tool_name,retry_state,recovery_state,child_activity_state,child_activity_count FROM local_workspace_tool_metadata LIMIT 1;",
                "subagent" => "INSERT INTO local_workspace_subagent_lifecycle SELECT $node,selected_state,started_state,completed_state,failed_state,deselected_state,input_state FROM local_workspace_subagent_lifecycle LIMIT 1;",
                _ => "INSERT INTO local_workspace_skill_metadata VALUES($node,'stale','not_observed',NULL,'not_observed',NULL,'unavailable',NULL,'not_observed',NULL,'fixture-generation');",
            };
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() =>
            CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorAcceptsSixteenSdkToolReferencesAndRejectsSeventeen()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string otelRunId = "018f0000-0000-7000-8000-000000000010";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        const string startEventId = "018f0000-0000-7000-8000-000000000031";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId, otelRunId, sdkRunId);
        using (var connection = OpenFile(temp.DatabasePath))
        {
            for (var index = 0; index < 14; index++)
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

            Assert.Equal(["recorded:inconsistent:not_observed:16"],
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

        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                INSERT INTO session_events(
                  event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                  parent_event_id,type,occurred_at,content_state)
                VALUES('018f0000-0000-7000-8000-000000000999','{{sessionId}}','{{sdkRunId}}',
                  'copilot-sdk','copilot-sdk-stream','sdk-tool-overflow-16','{{startEventId}}',
                  'tool.execution_complete','2026-08-26T00:00:59.0000000+00:00','not_captured');
                """);
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
        }
        var overflow = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() =>
            CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());
        Assert.Equal("workspace_too_large", overflow.Error);
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
    public async Task ProductionCoordinatorRejectsACaseVariantDuplicateOtelParentSpanOwner()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE monitor_spans SET parent_span_id='cccccccccccccccc'
                WHERE raw_record_id=1 AND trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('018f0000-0000-7000-8000-000000000012','018f0000-0000-7000-8000-000000000001','018f0000-0000-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cccccccccccccccc','otel.span','2026-08-26T00:00:01.0000000+00:00','not_captured');
                INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
                  VALUES(2,'raw-otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-26T00:00:01.0000000+00:00','{}','{}',1,randomblob(32));
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,status,start_time,end_time,projected_at)
                  VALUES(2,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','cccccccccccccccc',NULL,0,'chat','internal','ok','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:01.0000000+00:00','2026-08-26T00:00:01.0000000+00:00');
                """);
            using (var transaction = connection.BeginTransaction())
            {
                LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                    DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
                transaction.Commit();
            }
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='otel';
                """).Single();
            Assert.Equal(["exact:recorded"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node.relationship_authority||':'||metadata.caller_state
                FROM local_workspace_nodes node JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
                WHERE node.node_id=(SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool' AND source_family='otel');
                """));
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
                  VALUES(3,'raw-otlp','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','2026-08-26T00:00:01.5000000+00:00','{}','{}',1,randomblob(32));
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,status,start_time,end_time,projected_at)
                  VALUES(3,'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','CCCCCCCCCCCCCCCC',NULL,0,'chat','internal','ok','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:01.0000000+00:00','2026-08-26T00:00:01.5000000+00:00');
                """);
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None).AsTask());
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorKeepsRetryNotObservedWhenTheOtelOwnerIsCaseVariantAmbiguous()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE monitor_spans SET operation='chat',category='llm_call',input_tokens=99
                WHERE raw_record_id=1 AND span_ordinal=0;
                INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens)
                  VALUES(1,0,2,99);
                INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
                  VALUES(2,'raw-otlp','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','2026-08-26T00:00:04.5000000+00:00','{}','{}',1,randomblob(32));
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,input_tokens,status,start_time,end_time,projected_at)
                  VALUES(2,'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','BBBBBBBBBBBBBBBB',NULL,0,'chat','llm_call',99,'ok',
                    '2026-08-26T00:00:02.0000000+00:00','2026-08-26T00:00:04.0000000+00:00','2026-08-26T00:00:04.5000000+00:00');
                """);
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            Assert.Equal(["not_observed:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT retry_activity_state||':'||COALESCE(CAST(retry_activity_count AS TEXT),'null')
                FROM local_workspace_execution_headers
                WHERE source_identity='018f0000-0000-7000-8000-000000000010';
                """));
            Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT source_identity FROM local_workspace_token_observations WHERE authority='llm_span';"));
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        var execution = Assert.Single(detail.Detail.Executions,
            static value => value.SourceIdentity == "018f0000-0000-7000-8000-000000000010");
        Assert.Equal("not_observed", execution.Activity.Retry.State);
        Assert.Null(execution.Activity.Retry.Value);
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
    [InlineData("DELETE FROM local_workspace_session_activity WHERE kind='tool';")]
    [InlineData("UPDATE local_workspace_token_observations SET input_tokens=-1;")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='unknown';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_expired,raw_content_expired';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_not_captured,projection_invalid';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='';")]
    [InlineData("UPDATE local_workspace_sessions SET revision_seed='';")]
    [InlineData("UPDATE local_workspace_sessions SET revision_seed='forged';")]
    [InlineData("UPDATE local_workspace_sessions SET status='failed';")]
    [InlineData("UPDATE local_workspace_sessions SET completeness='partial';")]
    [InlineData("UPDATE local_workspace_sessions SET sort_epoch_ms=sort_epoch_ms+1;")]
    [InlineData("UPDATE local_workspace_sessions SET started_at='2026-08-26T00:00:01.0000000+00:00',duration_ms=5000,sort_epoch_ms=sort_epoch_ms+1000;")]
    [InlineData("UPDATE local_workspace_sessions SET last_seen_at='2026-08-26T00:00:07.0000000+00:00',last_seen_epoch_ms=last_seen_epoch_ms+1000;")]
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
    [InlineData("source_forged")]
    [InlineData("source_missing")]
    [InlineData("model_forged")]
    [InlineData("model_missing")]
    [InlineData("activity_forged")]
    public async Task ProductionCoordinatorRejectsCoherentSessionSetAndActivityOwnerDrift(string corruption)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE session_runs SET model='model-a' WHERE run_id='018f0000-0000-7000-8000-000000000020';");
            using (var refresh = connection.BeginTransaction())
            {
                LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                    DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
                refresh.Commit();
            }
            LocalWorkspaceProjectionSchemaTests.Execute(connection, corruption switch
            {
                "source_forged" => "UPDATE local_workspace_session_sources SET source='vscode' WHERE source=(SELECT source FROM local_workspace_session_sources ORDER BY source COLLATE BINARY LIMIT 1);",
                "source_missing" => "DELETE FROM local_workspace_session_sources WHERE source=(SELECT source FROM local_workspace_session_sources ORDER BY source COLLATE BINARY LIMIT 1);",
                "model_forged" => "UPDATE local_workspace_session_models SET model='model-forged';",
                "model_missing" => "DELETE FROM local_workspace_session_models;",
                _ => "UPDATE local_workspace_session_activity SET count=999 WHERE kind='tool';",
            });
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() =>
            CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
                new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task SessionContributorRejectsALaterEligibleInstructionForgedAsTheFirstLabel()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeLabelSummaryFixture(temp.DatabasePath, sessionId);
        using var connection = OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE local_workspace_sessions SET
              label_text='Later instruction',
              label_source_identity='018f0000-0000-7000-8000-000000000022',
              revision_seed=replace(revision_seed,
                '018f0000-0000-7000-8000-000000000021','018f0000-0000-7000-8000-000000000022');
            UPDATE local_workspace_session_search_facts SET
              source_identity='018f0000-0000-7000-8000-000000000022',
              normalized_text='later instruction'
            WHERE kind='label';
            """);
        using var transaction = connection.BeginTransaction(deferred: true);
        var contributor = new LocalWorkspaceSessionSnapshotContributor(
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z")),
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => contributor.ReadAsync(
            new DirectReadTransaction(connection, transaction),
            new(LocalRepositoryScopeKind.All, null, sessionId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task DetailInstructionCountUsesTheExactAcceptedInstructionCarrierSet()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeLabelSummaryFixture(temp.DatabasePath, sessionId);
        using var connection = OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE session_events SET type='userPromptSubmitted'
            WHERE session_id='018f0000-0000-7000-8000-000000000001';
            UPDATE session_event_content SET content_json=CASE event_id
              WHEN '018f0000-0000-7000-8000-000000000021' THEN '{"prompt":"First instruction"}'
              ELSE '{"prompt":"Later instruction"}' END;
            """);
        foreach (var (eventId, itemId, capturedAt) in new[]
                 {
                     ("018f0000-0000-7000-8000-000000000021", "label-item-1", "2026-08-26T00:00:01.0000000+00:00"),
                     ("018f0000-0000-7000-8000-000000000022", "label-item-2", "2026-08-26T00:00:02.0000000+00:00"),
                 })
        {
            string storeId;
            byte[] ownerToken;
            using (var owner = connection.CreateCommand())
            {
                owner.CommandText = "SELECT i.store_instance_id,c.retention_owner_token FROM retention_items i JOIN session_event_content c ON c.event_id=i.source_item_id WHERE i.item_id=$item;";
                owner.Parameters.AddWithValue("$item", itemId);
                using var reader = owner.ExecuteReader();
                Assert.True(reader.Read());
                storeId = reader.GetString(0);
                ownerToken = (byte[])reader.GetValue(1);
            }
            const string expiresAt = "2026-09-01T00:00:00.0000000+00:00";
            var receipt = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOwnershipReceipt.CreateSession(new(
                storeId, eventId, "application/json", capturedAt, DateTimeOffset.Parse(capturedAt).UtcTicks,
                expiresAt, DateTimeOffset.Parse(expiresAt).UtcTicks, sessionId, null,
                "copilot-sdk-stream", eventId.EndsWith("21", StringComparison.Ordinal) ? "first-label" : "later-label", ownerToken));
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE retention_items SET ownership_receipt=$receipt WHERE item_id=$item;";
            update.Parameters.AddWithValue("$receipt", receipt);
            update.Parameters.AddWithValue("$item", itemId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        using (var refresh = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }
        using var transaction = connection.BeginTransaction(deferred: true);
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load(),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:01Z")));

        var detail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);

        Assert.Equal("018f0000-0000-7000-8000-000000000021", detail.InstructionSourceIdentity);
        Assert.Equal(1, detail.InstructionAdditionalCount);
    }

    [Theory]
    [InlineData("{\"value\":\"\"}")]
    [InlineData("{\"value\":\" \\n \\t\"}")]
    [InlineData("{\"value\":{}}")]
    [InlineData("not-json")]
    public async Task ProjectionChoosesTheFirstNonemptyInstructionBeforeRankingAndCounting(string firstContent)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeLabelSummaryFixture(temp.DatabasePath, sessionId);
        using var connection = OpenFile(temp.DatabasePath);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE session_event_content SET content_json=$content WHERE event_id='018f0000-0000-7000-8000-000000000021';";
            update.Parameters.AddWithValue("$content", firstContent);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        using (var refresh = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(connection, refresh,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            refresh.Commit();
        }

        Assert.Equal(["018f0000-0000-7000-8000-000000000022:Later instruction:1"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT label_source_identity||':'||label_text||':'||CAST(instruction_count AS TEXT) FROM local_workspace_sessions;"));
        using var read = connection.BeginTransaction(deferred: true);
        var detail = await new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load(),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:01Z"))).ReadAsync(
            new DirectReadTransaction(connection, read),
            new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        Assert.Equal("018f0000-0000-7000-8000-000000000022", detail.InstructionSourceIdentity);
        Assert.Equal(0, detail.InstructionAdditionalCount);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("missing")]
    public async Task SessionContributorRejectsSearchFactOwnerAndCardinalityDrift(string corruption)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeLabelSummaryFixture(temp.DatabasePath, sessionId);
        using var connection = OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, corruption == "forged" ? """
            INSERT INTO local_workspace_session_search_facts(
              session_id,kind,source_identity,normalized_text,expires_at)
            VALUES('018f0000-0000-7000-8000-000000000001','tool','!forged','forged',NULL);
            """ : """
            DELETE FROM local_workspace_session_search_facts WHERE kind='label';
            """);
        using var transaction = connection.BeginTransaction(deferred: true);
        var contributor = new LocalWorkspaceSessionSnapshotContributor(
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z")),
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(() => contributor.ReadAsync(
            new DirectReadTransaction(connection, transaction),
            new(LocalRepositoryScopeKind.All, null, sessionId), CancellationToken.None).AsTask());

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("missing")]
    public async Task SessionContributorRejectsTokenObservationOwnerAndCardinalityDrift(string corruption)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        using var connection = OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, corruption == "forged" ? """
            INSERT INTO local_workspace_token_observations(
              session_id,execution_id,authority,authority_rank,source_identity,input_tokens,
              output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
            SELECT session_id,execution_id,'session_run',0,'!forged',999,
                   output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens
            FROM local_workspace_token_observations WHERE authority='session_run'
            ORDER BY source_identity COLLATE BINARY LIMIT 1;
            """ : """
            DELETE FROM local_workspace_token_observations
            WHERE rowid=(SELECT rowid FROM local_workspace_token_observations
              WHERE authority='session_run' ORDER BY source_identity COLLATE BINARY LIMIT 1);
            """);
        using var transaction = connection.BeginTransaction(deferred: true);
        var contributor = new LocalWorkspaceSessionSnapshotContributor(
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:10:00Z")),
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositoryScopeKind.All, null, sessionId), CancellationToken.None));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorEmitsInverseSdkLifecycleReferencesInClosedKeyOrder()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string startEventId = "018f0000-0000-7000-8000-000000000039";
        const string completionEventId = "018f0000-0000-7000-8000-000000000032";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", "018f0000-0000-7000-8000-000000000020");
        string nodeId;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                PRAGMA foreign_keys=OFF;
                UPDATE session_events SET event_id='{{startEventId}}'
                WHERE event_id='018f0000-0000-7000-8000-000000000031';
                UPDATE session_events SET parent_event_id='{{startEventId}}'
                WHERE event_id='{{completionEventId}}';
                PRAGMA foreign_keys=ON;
                """);
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='session_sdk';
                """).Single();
            Assert.Equal([startEventId, completionEventId], LocalWorkspaceProjectionSchemaTests.Strings(connection, $$"""
                SELECT event_id FROM local_workspace_node_source_references
                WHERE node_id='{{nodeId}}' ORDER BY source_ordinal;
                """));
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal([completionEventId, startEventId], tool.SourceReferences!.Select(static reference => reference.EventId));
    }

    [Theory]
    [InlineData("absent", "exact")]
    [InlineData("missing", "unknown")]
    [InlineData("cross_run", "unknown")]
    public async Task ProductionCoordinatorDistinguishesAbsentAndUnresolvedSdkToolCallers(
        string caller,
        string relationshipAuthority)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string sdkRunId = "018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010", sdkRunId);
        string nodeId;
        string expectedParent;
        using (var connection = OpenFile(temp.DatabasePath))
        {
            if (caller == "missing")
                LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                    PRAGMA foreign_keys=OFF;
                    UPDATE session_events SET parent_event_id='missing-sdk-caller'
                    WHERE event_id='018f0000-0000-7000-8000-000000000031';
                    PRAGMA foreign_keys=ON;
                    """);
            else if (caller == "cross_run")
                LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                    INSERT INTO session_events(
                      event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
                    VALUES('018f0000-0000-7000-8000-000000000029','018f0000-0000-7000-8000-000000000001',
                      '018f0000-0000-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream',
                      'cross-run-sdk-caller','event','2026-08-26T00:00:02.5000000+00:00','not_captured');
                    UPDATE session_events SET parent_event_id='018f0000-0000-7000-8000-000000000029'
                    WHERE event_id='018f0000-0000-7000-8000-000000000031';
                    """);
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                DateTimeOffset.Parse("2026-08-26T00:10:01Z"), FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
            nodeId = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node_id FROM local_workspace_semantic_receipts
                WHERE semantic_kind='tool' AND source_family='session_sdk';
                """).Single();
            expectedParent = LocalWorkspaceProjectionSchemaTests.Strings(connection, caller == "absent" ? $$"""
                SELECT node_id FROM local_workspace_nodes
                WHERE source_kind='execution_root' AND source_identity='{{sdkRunId}}';
                """ : $$"""
                SELECT node_id FROM local_workspace_nodes
                WHERE source_kind='unknown_relation_group' AND source_identity='{{sdkRunId}}';
                """).Single();
            Assert.Equal([$"{expectedParent}:{relationshipAuthority}:not_observed:null"],
                LocalWorkspaceProjectionSchemaTests.Strings(connection, $$"""
                    SELECT node.parent_node_id||':'||node.relationship_authority||':'||metadata.caller_state||':'||
                           COALESCE(metadata.caller_node_id,'null')
                    FROM local_workspace_nodes node
                    JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
                    WHERE node.node_id='{{nodeId}}';
                    """));
            Assert.Equal(caller == "absent" ? ["1"] : ["0"],
                LocalWorkspaceProjectionSchemaTests.Strings(connection, $$"""
                    SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_node_edges
                    WHERE node_id='{{nodeId}}' AND relation_kind='parent' AND relationship_authority='exact';
                    """));
        }

        var detail = await CreateRoundFiveService(temp.DatabasePath).ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: nodeId), CancellationToken.None);

        var tool = Assert.Single(detail.Detail.Nodes, value => value.NodeId == nodeId);
        Assert.Equal(expectedParent, tool.ParentNodeId);
        Assert.Equal(relationshipAuthority, tool.RelationshipAuthority);
        Assert.Equal("not_observed", tool.ToolMetadata!.CallerState);
        Assert.Null(tool.ToolMetadata.CallerNodeId);
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
        var rawContentStatement = observed.Sql.FirstOrDefault(statement =>
            statement.Contains("content_json", StringComparison.OrdinalIgnoreCase));
        Assert.True(rawContentStatement is null, rawContentStatement);
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
    public async Task AiSourceContributionAdmitsIndependentRunAndEventLimitsBeyondCombinedNodeLimit()
    {
        using var temp=new MonitorTempDirectory();const string runA="018f0000-0000-7000-8000-000000000010";const string runB="018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,runA,runB);using var connection=OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection,"INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,status,projected_at) VALUES(2,'ffffffffffffffffffffffffffffffff','eeeeeeeeeeeeeeee',0,'outside','error','error','2026-08-26T00:00:00.0000000+00:00');");
        var existing=Convert.ToInt32(LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT CAST(COUNT(*) AS TEXT) FROM session_events;").Single());
        for (var index = existing; index < 4095; index++)
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $"INSERT INTO session_events(event_id,session_id,run_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('ai-event-{index:D4}','{SessionId}','{runA}','synthetic','ai-source-{index:D4}','event','2026-08-26T00:00:00.0000000+00:00','not_captured');");
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);using var transaction = connection.BeginTransaction();
        var authority = FixedSkillRegistryGenerationAuthority.Load(); using var pinned = LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(authority)!;
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority);

        var result = await contributor.ReadAiProjectionPinnedAsync(new DirectReadTransaction(connection, transaction), SessionId, null,
            DateTimeOffset.UnixEpoch, pinned, CancellationToken.None);

        Assert.Equal(2, result.Input.Executions.Count);
        Assert.Equal(4095, result.Input.SourceEventCount);
        Assert.True(result.Input.Nodes.Count > 4096);
        Assert.Equal("completed",result.Input.SessionFacts.GetValueOrDefault().GetProperty("status").GetString());
        Assert.Contains(result.Input.SanitizedSpanObservations,span=>span.Contains("\"tool_name\":\"Read\"",StringComparison.Ordinal));
        Assert.DoesNotContain(result.Input.SanitizedSpanObservations,span=>span.Contains("outside",StringComparison.Ordinal));
        Assert.Contains(result.Input.Nodes,node=>node.Metadata is { } metadata&&metadata.TryGetProperty("subagent_activity",out _));
        Assert.Throws<LocalAiScopeTooLargeException>(()=>LocalAiSnapshotProjectionBuilderV1.BuildSession(result.Input));
    }

    [Theory]
    [InlineData("readable", "2026-08-27T00:00:00Z", "available")]
    [InlineData("expired", "2026-09-02T00:00:00Z", "expired")]
    [InlineData("read_denied", "2026-08-27T00:00:00Z", "read_denied")]
    [InlineData("deleted", "2026-08-27T00:00:00Z", "deleted")]
    public async Task AiSourceContributionUsesEffectiveContentAuthorityAtAcceptedInstant(
        string transition, string acceptedAtText, string expected)
    {
        using var temp=new MonitorTempDirectory();const string runA="018f0000-0000-7000-8000-000000000010";const string runB="018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,runA,runB);using var connection=OpenFile(temp.DatabasePath);
        AddAiReadableContent(connection,SessionId,runB);
        if(transition=="read_denied")
            LocalWorkspaceProjectionSchemaTests.Execute(connection,"UPDATE retention_items SET state='deletion_queued',read_denied_at='2026-08-27T00:00:00.0000000+00:00',queued_at='2026-08-27T00:00:00.0000000+00:00' WHERE store_kind='session_event_content';");
        if(transition=="deleted")
            LocalWorkspaceProjectionSchemaTests.Execute(connection,"UPDATE retention_items SET state='deleted',deleted_at='2026-08-27T00:00:00.0000000+00:00' WHERE store_kind='session_event_content'; INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'2026-08-27T00:00:00.0000000+00:00','2026-08-27T00:00:00.0000000+00:00' FROM retention_items WHERE store_kind='session_event_content'; DELETE FROM session_event_content;");
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);using var transaction=connection.BeginTransaction();
        var authority=FixedSkillRegistryGenerationAuthority.Load();using var pinned=LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(authority)!;
        var contributor=new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority);

        var result=await contributor.ReadAiProjectionPinnedAsync(new DirectReadTransaction(connection,transaction),SessionId,null,
            DateTimeOffset.Parse(acceptedAtText),pinned,CancellationToken.None);

        var target=Assert.Single(result.Input.RawEvidence!,item=>item.Locator.SourceItemId=="018f0000-0000-7000-8000-000000000025");
        Assert.Equal(expected,target.Locator.State);
    }

    [Theory]
    [InlineData(257, 0)]
    [InlineData(1, 4097)]
    public async Task AiSourceContributionRejectsEachIndependentOneOver(int runCount, int eventCount)
    {
        using var temp=new MonitorTempDirectory();const string runA="018f0000-0000-7000-8000-000000000010";const string runB="018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,runA,runB);using var connection=OpenFile(temp.DatabasePath);
        var existingRuns=Convert.ToInt32(LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT CAST(COUNT(*) AS TEXT) FROM session_runs;").Single());
        var existingEvents=Convert.ToInt32(LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT CAST(COUNT(*) AS TEXT) FROM session_events;").Single());
        for(var index=existingRuns;index<runCount;index++)LocalWorkspaceProjectionSchemaTests.Execute(connection,$"INSERT INTO session_runs VALUES('ai-run-{index:D4}','{SessionId}','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        for (var index=existingEvents;index<eventCount;index++) LocalWorkspaceProjectionSchemaTests.Execute(connection,$"INSERT INTO session_events(event_id,session_id,run_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('ai-event-{index:D4}','{SessionId}','{runA}','synthetic','ai-source-{index:D4}','event','2026-08-26T00:00:00.0000000+00:00','not_captured');");
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);using var transaction=connection.BeginTransaction();
        var authority=FixedSkillRegistryGenerationAuthority.Load();using var pinned=LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(authority)!;
        var contributor=new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority);
        var error=await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(()=>contributor.ReadAiProjectionPinnedAsync(
            new DirectReadTransaction(connection,transaction),SessionId,null,DateTimeOffset.UnixEpoch,pinned,CancellationToken.None).AsTask());
        Assert.Equal("workspace_too_large",error.Error);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public async Task AiSourceContributionBoundsRealSanitizedSpanFacts(int spanCount, bool rejected)
    {
        using var temp=new MonitorTempDirectory();const string runA="018f0000-0000-7000-8000-000000000010";const string runB="018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,runA,runB);using var connection=OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection,"DELETE FROM monitor_spans;");
        for(var index=0;index<spanCount;index++)LocalWorkspaceProjectionSchemaTests.Execute(connection,$"INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,status,projected_at) VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','{index:x16}',{index},'chat','llm_call','ok','2026-08-26T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);using var transaction=connection.BeginTransaction();
        var authority=FixedSkillRegistryGenerationAuthority.Load();using var pinned=LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(authority)!;
        var contributor=new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority);

        if(rejected)
        {
            var error=await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(()=>contributor.ReadAiProjectionPinnedAsync(
                new DirectReadTransaction(connection,transaction),SessionId,null,DateTimeOffset.UnixEpoch,pinned,CancellationToken.None).AsTask());
            Assert.Equal("workspace_too_large",error.Error);
        }
        else
        {
            var result=await contributor.ReadAiProjectionPinnedAsync(new DirectReadTransaction(connection,transaction),SessionId,null,
                DateTimeOffset.UnixEpoch,pinned,CancellationToken.None);
            Assert.Equal(4096,result.Input.SanitizedSpanObservations.Count);
            Assert.Contains(result.Input.SanitizedSpanObservations,span=>span.Contains("\"operation\":\"chat\"",StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AiSourceContributionProjectsBoundedSanitizedSpanWithExactNodeOwner()
    {
        using var temp=new MonitorTempDirectory();const string runA="018f0000-0000-7000-8000-000000000010";const string runB="018f0000-0000-7000-8000-000000000020";
        InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,runA,runB);using var connection=OpenFile(temp.DatabasePath);
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);using var transaction=connection.BeginTransaction();
        var authority=FixedSkillRegistryGenerationAuthority.Load();using var pinned=LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(authority)!;
        var contributor=new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority);

        var result=await contributor.ReadAiProjectionPinnedAsync(new DirectReadTransaction(connection,transaction),SessionId,null,
            DateTimeOffset.UnixEpoch,pinned,CancellationToken.None);
        var snapshot=LocalAiSnapshotProjectionBuilderV1.BuildSession(result.Input);

        using var payload=JsonDocument.Parse(snapshot.PayloadCanonicalJson);
        var fact=Assert.Single(payload.RootElement.GetProperty("sanitized_span_observations").EnumerateArray());
        var citation=fact.GetProperty("citation_ref").GetString();
        Assert.Equal("Read",fact.GetProperty("observation").GetProperty("tool_name").GetString());
        var exactOwner=Assert.Single(result.Input.Nodes,node=>node.SanitizedSpanObservation is { } observation
            && observation.Contains("\"tool_name\":\"Read\"",StringComparison.Ordinal));
        Assert.Equal(exactOwner.NodeId,citation);
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
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-1','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        using (var schemaTransaction = connection.BeginTransaction())
        {
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, schemaTransaction);
            schemaTransaction.Commit();
        }
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

    internal static void InitializeRoundFiveSemanticFixture(
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

    private static void AddAiReadableContent(SqliteConnection connection,string sessionId,string runId)
    {
        const string eventId="018f0000-0000-7000-8000-000000000025",captured="2026-08-26T00:00:05.2500000+00:00",expires="2026-09-01T00:00:00.0000000+00:00";
        LocalWorkspaceProjectionSchemaTests.Execute(connection,$$"""
            UPDATE session_events SET content_state='available' WHERE event_id='{{eventId}}';
            INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES('{{eventId}}','application/json','{"value":"raw"}','{{captured}}','{{expires}}',randomblob(32));
            INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version)
            SELECT 'ai-content-item',store_instance_id,'session_event_content','{{eventId}}',1,randomblob(32),'{{captured}}','{{expires}}','raw-default-90d',1,'expiring',1,1
            FROM retention_store_instances WHERE id=1;
            """);
        string storeId;byte[] token;using(var owner=connection.CreateCommand()){owner.CommandText="SELECT i.store_instance_id,c.retention_owner_token FROM retention_items i JOIN session_event_content c ON c.event_id=i.source_item_id WHERE i.item_id='ai-content-item';";using var reader=owner.ExecuteReader();Assert.True(reader.Read());storeId=reader.GetString(0);token=(byte[])reader.GetValue(1);}
        var receipt=CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOwnershipReceipt.CreateSession(new(storeId,eventId,"application/json",captured,DateTimeOffset.Parse(captured).UtcTicks,expires,DateTimeOffset.Parse(expires).UtcTicks,sessionId,runId,"synthetic","generic-event",token));
        using(var update=connection.CreateCommand()){update.CommandText="UPDATE retention_items SET ownership_receipt=$receipt WHERE item_id='ai-content-item';";update.Parameters.AddWithValue("$receipt",receipt);Assert.Equal(1,update.ExecuteNonQuery());}
        using var transaction=connection.BeginTransaction();LocalWorkspaceProjectionStore.Refresh(connection,transaction,DateTimeOffset.Parse("2026-08-26T00:10:01Z"),FixedSkillRegistryGenerationAuthority.Load());transaction.Commit();
    }

    private static void InitializeLabelSummaryFixture(string databasePath, string sessionId)
    {
        using (var connection = OpenFile(databasePath))
        using (var transaction = connection.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            transaction.Commit();
        }
        new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(databasePath).CreateSchema();
        using var setup = OpenFile(databasePath);
        using (var transaction = setup.BeginTransaction())
        {
            SkillProjectionSchemaV1.Ensure(setup, transaction);
            CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot.SkillInvocationSnapshotSchemaV1.Ensure(setup, transaction);
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(setup, $$"""
            INSERT INTO sessions VALUES(
              '{{sessionId}}','completed','full',NULL,NULL,NULL,NULL,
              '2026-08-26T00:00:00.0000000+00:00','expiring',
              '2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:02.0000000+00:00');
            INSERT INTO session_events(
              event_id,session_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
            VALUES
              ('018f0000-0000-7000-8000-000000000021','{{sessionId}}','copilot-sdk','copilot-sdk-stream',
               'first-label','user.message','2026-08-26T00:00:01.0000000+00:00','available'),
              ('018f0000-0000-7000-8000-000000000022','{{sessionId}}','copilot-sdk','copilot-sdk-stream',
               'later-label','user.message','2026-08-26T00:00:02.0000000+00:00','available');
            INSERT INTO session_event_content(
              event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES
              ('018f0000-0000-7000-8000-000000000021','application/json','{"value":"First instruction"}',
               '2026-08-26T00:00:01.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32)),
              ('018f0000-0000-7000-8000-000000000022','application/json','{"value":"Later instruction"}',
               '2026-08-26T00:00:02.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            INSERT INTO retention_items(
              item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,
              captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version)
            SELECT 'label-item-1',store_instance_id,'session_event_content',
                   '018f0000-0000-7000-8000-000000000021',1,randomblob(32),
                   '2026-08-26T00:00:01.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',
                   'raw-default-90d',1,'expiring',1,1
            FROM retention_store_instances WHERE id=1;
            INSERT INTO retention_items(
              item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,
              captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version)
            SELECT 'label-item-2',store_instance_id,'session_event_content',
                   '018f0000-0000-7000-8000-000000000022',1,randomblob(32),
                   '2026-08-26T00:00:02.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',
                   'raw-default-90d',1,'expiring',1,1
            FROM retention_store_instances WHERE id=1;
            """);
        foreach (var (eventId, itemId, sourceEventId, capturedAt) in new[]
                 {
                     ("018f0000-0000-7000-8000-000000000021", "label-item-1", "first-label", "2026-08-26T00:00:01.0000000+00:00"),
                     ("018f0000-0000-7000-8000-000000000022", "label-item-2", "later-label", "2026-08-26T00:00:02.0000000+00:00"),
                 })
        {
            using var owner = setup.CreateCommand();
            owner.CommandText = "SELECT i.store_instance_id,c.retention_owner_token FROM retention_items i JOIN session_event_content c ON c.event_id=i.source_item_id WHERE i.item_id=$item;";
            owner.Parameters.AddWithValue("$item", itemId);
            using var reader = owner.ExecuteReader();
            Assert.True(reader.Read());
            var storeId = reader.GetString(0);
            var ownerToken = (byte[])reader.GetValue(1);
            reader.Close();
            const string expiresAt = "2026-09-01T00:00:00.0000000+00:00";
            var receipt = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOwnershipReceipt.CreateSession(new(
                storeId, eventId, "application/json", capturedAt, DateTimeOffset.Parse(capturedAt).UtcTicks,
                expiresAt, DateTimeOffset.Parse(expiresAt).UtcTicks, sessionId, null,
                "copilot-sdk-stream", sourceEventId, ownerToken));
            using var update = setup.CreateCommand();
            update.CommandText = "UPDATE retention_items SET ownership_receipt=$receipt WHERE item_id=$item;";
            update.Parameters.AddWithValue("$receipt", receipt);
            update.Parameters.AddWithValue("$item", itemId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        LocalWorkspaceProjectionSchemaV1.Ensure(setup, DateTimeOffset.Parse("2026-08-26T00:10:00Z"));
        Assert.Equal(["018f0000-0000-7000-8000-000000000021:First instruction:2"],
            LocalWorkspaceProjectionSchemaTests.Strings(setup,
                "SELECT label_source_identity||':'||label_text||':'||CAST(instruction_count AS TEXT) FROM local_workspace_sessions;"));
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

    private sealed class ThrowingCanonicalRegistryAuthority : ISkillRegistryGenerationAuthority
    {
        internal int DisposedLeaseCount { get; private set; }

        public ISkillRegistryGenerationCapture CaptureGeneration() => new Capture();

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = new Lease(this);
            return true;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) => capture is Capture && lease is Lease;

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) => throw new InvalidOperationException("canonical_identity_failure");

        public bool IsProducerTupleAccepted(
            ISkillRegistryGenerationLease lease,
            SkillRegistryProducerTuple tuple) => false;

        private sealed class Capture : ISkillRegistryGenerationCapture { }

        private sealed class Lease(ThrowingCanonicalRegistryAuthority owner) : ISkillRegistryGenerationLease
        {
            public void Dispose() => owner.DisposedLeaseCount++;
        }
    }

    internal sealed class NativeDetailObserver : IDisposable
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

    private sealed class CountingTimeProvider(DateTimeOffset value) : TimeProvider
    {
        internal int CallCount { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return value;
        }
    }
}

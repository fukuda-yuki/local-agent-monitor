using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionDetailSnapshotContributor : ILocalWorkspaceSessionDetailSnapshotContributor, ILocalWorkspaceComparisonDetailSnapshotContributor
{
    internal const string BoundsSql = """
        SELECT
          EXISTS(SELECT 1 FROM local_workspace_execution_headers WHERE session_id=$session_id LIMIT 1 OFFSET 256),
          EXISTS(SELECT 1 FROM local_workspace_nodes WHERE session_id=$session_id LIMIT 1 OFFSET 4096)
            OR COALESCE((SELECT node_overflow FROM local_workspace_sessions WHERE session_id=$session_id),0),
          EXISTS(SELECT 1 FROM (
            SELECT run_id source_identity FROM session_runs WHERE session_id=$session_id
            UNION ALL
            SELECT event_id FROM session_events WHERE session_id=$session_id AND run_id IS NOT NULL)
            LIMIT 1 OFFSET 4096);
        """;
    private const int MaximumExecutions = 256;
    private const int MaximumNodes = 4096;
    private readonly Action<string>? statementObserver;
    private readonly ISkillRegistryGenerationAuthority? registryAuthority;
    private readonly TimeProvider timeProvider;

    internal LocalWorkspaceSessionDetailSnapshotContributor(
        Action<string>? statementObserver = null,
        ISkillRegistryGenerationAuthority? registryAuthority = null,
        TimeProvider? timeProvider = null)
    {
        this.statementObserver = statementObserver;
        this.registryAuthority = registryAuthority;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal TimeProvider TimeProvider => timeProvider;
    internal ISkillRegistryGenerationAuthority? RegistryAuthority => registryAuthority;

    public async ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        CancellationToken cancellationToken)
    {
        var acceptedAt = timeProvider.GetUtcNow();
        using var pinnedRegistry = registryAuthority is null
            ? null
            : PinnedRegistryAuthority.TryCreate(registryAuthority);
        if (pinnedRegistry is null)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        return await transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadAsync(connection, sqliteTransaction, request, acceptedAt, pinnedRegistry, revisionSession: null, token), cancellationToken);
    }

    internal ValueTask<LocalWorkspaceSessionDetailContribution> ReadPinnedAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        DateTimeOffset acceptedAt,
        PinnedRegistryAuthority pinnedRegistry,
        LocalRepositoryScopeSessionSnapshot? revisionSession,
        CancellationToken cancellationToken) =>
        transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadAsync(connection, sqliteTransaction, request, acceptedAt, pinnedRegistry, revisionSession, token), cancellationToken);

    internal ValueTask<LocalAiProjectionContributionV1> ReadAiProjectionPinnedAsync(
        ILocalRepositoryReadTransaction transaction, string sessionId, string? nodeId, DateTimeOffset acceptedAt,
        PinnedRegistryAuthority pinnedRegistry, CancellationToken cancellationToken) =>
        transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadAiProjectionAsync(connection, sqliteTransaction, sessionId, nodeId, acceptedAt, pinnedRegistry, token), cancellationToken);

    private static async ValueTask<LocalAiProjectionContributionV1> ReadAiProjectionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId, string? nodeId,
        DateTimeOffset acceptedAt, PinnedRegistryAuthority pinnedRegistry, CancellationToken token)
    {
        using (var bounds = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM session_runs WHERE session_id=$session_id LIMIT 1 OFFSET 256),
              EXISTS(SELECT 1 FROM session_events WHERE session_id=$session_id LIMIT 1 OFFSET 4096);
            """, sessionId))
        using (var reader = await bounds.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0)
                throw new LocalWorkspaceSessionDetailException("workspace_too_large");
        }
        var currentSkills = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, [sessionId], acceptedAt, pinnedRegistry);
        currentSkills.TryGetValue(sessionId, out var skillProjection);
        if (skillProjection?.State == "unavailable") throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var revision = await ReadCanonicalRevisionInput(connection, transaction, sessionId, acceptedAt,
            skillProjection, pinnedRegistry.CanonicalIdentity, token);
        var executions = new List<string>(); var nodes = new List<LocalAiProjectionNodeV1>();
        using (var runs = Command(connection, transaction,"""
            SELECT run_id,source_surface,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status
            FROM session_runs WHERE session_id=$session_id ORDER BY run_id LIMIT 257;
            """, sessionId))
        using (var reader = await runs.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var runId=reader.GetString(0);var executionId=LocalWorkspaceProjectionStore.StableExecutionId(sessionId,"session_run",runId);
                executions.Add(executionId);nodes.Add(new(LocalWorkspaceProjectionStore.StableNodeId("execution_root",runId),executionId,null,[],
                    System.Text.Json.JsonSerializer.SerializeToElement(new{kind="execution",source_kind="session_run",
                        source_surface=reader.IsDBNull(1)?null:reader.GetString(1),model=reader.IsDBNull(2)?null:reader.GetString(2),
                        started_at=reader.IsDBNull(3)?null:reader.GetString(3),ended_at=reader.IsDBNull(4)?null:reader.GetString(4),
                        input_tokens=reader.IsDBNull(5)?(long?)null:reader.GetInt64(5),output_tokens=reader.IsDBNull(6)?(long?)null:reader.GetInt64(6),
                        total_tokens=reader.IsDBNull(7)?(long?)null:reader.GetInt64(7),status=reader.GetString(8)})));
            }
        var events = new List<(string EventId,string? RunId,string? ParentId,string? TraceId,string SourceEventId,string Type,string OccurredAt,string ContentState)>();
        using (var command = Command(connection, transaction, """
            SELECT event_id,run_id,parent_event_id,trace_id,source_event_id,type,occurred_at,content_state
            FROM session_events WHERE session_id=$session_id ORDER BY event_id LIMIT 4097;
            """, sessionId))
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) events.Add((reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),
                reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7)));
        var spans=new List<string>();
        var spansByExecution=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        var spansByIdentity=new Dictionary<(string TraceId,string SpanId),string>();
        using(var spanCommand=Command(connection,transaction,"""
            SELECT r.run_id,m.trace_id,m.span_id,m.parent_span_id,m.operation,m.category,m.tool_name,m.tool_type,
              m.agent_name,m.request_model,m.response_model,m.input_tokens,m.output_tokens,m.total_tokens,m.reasoning_tokens,
              m.cache_read_tokens,m.cache_creation_tokens,m.status,m.error_type,m.duration_ms,m.start_time,m.end_time
            FROM session_runs r JOIN monitor_spans m ON m.trace_id=r.trace_id COLLATE BINARY
            WHERE r.session_id=$session_id
              AND (SELECT COUNT(*) FROM session_runs owner WHERE owner.session_id=r.session_id AND owner.trace_id=m.trace_id COLLATE BINARY)=1
              AND m.span_id IS NOT NULL
              AND (SELECT COUNT(*) FROM monitor_spans owner
                WHERE lower(owner.trace_id)=lower(m.trace_id) COLLATE BINARY AND lower(owner.span_id)=lower(m.span_id) COLLATE BINARY)=1
            ORDER BY m.trace_id,m.span_ordinal LIMIT 4097;
            """,sessionId))using(var reader=await spanCommand.ExecuteReaderAsync(token))while(await reader.ReadAsync(token))
        {
            var executionId=LocalWorkspaceProjectionStore.StableExecutionId(sessionId,"session_run",reader.GetString(0));
            var traceId=reader.GetString(1);var spanId=reader.GetString(2);
            var observation=System.Text.Json.JsonSerializer.Serialize(new{trace_id=traceId,span_id=spanId,
                parent_span_id=reader.IsDBNull(3)?null:reader.GetString(3),operation=reader.IsDBNull(4)?null:reader.GetString(4),
                category=reader.IsDBNull(5)?null:reader.GetString(5),tool_name=reader.IsDBNull(6)?null:reader.GetString(6),
                tool_type=reader.IsDBNull(7)?null:reader.GetString(7),agent_name=reader.IsDBNull(8)?null:reader.GetString(8),
                request_model=reader.IsDBNull(9)?null:reader.GetString(9),response_model=reader.IsDBNull(10)?null:reader.GetString(10),
                input_tokens=reader.IsDBNull(11)?(long?)null:reader.GetInt64(11),output_tokens=reader.IsDBNull(12)?(long?)null:reader.GetInt64(12),
                total_tokens=reader.IsDBNull(13)?(long?)null:reader.GetInt64(13),reasoning_tokens=reader.IsDBNull(14)?(long?)null:reader.GetInt64(14),
                cache_read_tokens=reader.IsDBNull(15)?(long?)null:reader.GetInt64(15),cache_creation_tokens=reader.IsDBNull(16)?(long?)null:reader.GetInt64(16),
                status=reader.IsDBNull(17)?null:reader.GetString(17),error_type=reader.IsDBNull(18)?null:reader.GetString(18),
                duration_ms=reader.IsDBNull(19)?(double?)null:reader.GetDouble(19),start_time=reader.IsDBNull(20)?null:reader.GetString(20),
                end_time=reader.IsDBNull(21)?null:reader.GetString(21)});
            spans.Add(observation);spansByIdentity.Add((traceId,spanId),observation);
            if(!spansByExecution.TryGetValue(executionId,out var owned))spansByExecution.Add(executionId,owned=[]);owned.Add(observation);
        }
        if(spans.Count>4096)throw new LocalWorkspaceSessionDetailException("workspace_too_large");
        foreach(var item in events)
        {
            var executionId=item.RunId is null?"unassigned":LocalWorkspaceProjectionStore.StableExecutionId(sessionId,"session_run",item.RunId);
            var parent=item.ParentId is not null?LocalWorkspaceProjectionStore.StableNodeId("session_event",item.ParentId):item.RunId is null?null:LocalWorkspaceProjectionStore.StableNodeId("execution_root",item.RunId);
            nodes.Add(new(LocalWorkspaceProjectionStore.StableNodeId("session_event",item.EventId),executionId,parent,[],
                System.Text.Json.JsonSerializer.SerializeToElement(new{kind="event",type=item.Type,occurred_at=item.OccurredAt,content_state=item.ContentState})));
        }
        var references=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        using(var edgeCommand=Command(connection,transaction,"""
            SELECT e.node_id,e.related_node_id FROM local_workspace_node_edges e JOIN local_workspace_nodes n ON n.node_id=e.node_id
            WHERE n.session_id=$session_id ORDER BY e.node_id,e.source_ordinal,e.related_node_id;
            """,sessionId))using(var reader=await edgeCommand.ExecuteReaderAsync(token))while(await reader.ReadAsync(token))
        {var owner=reader.GetString(0);if(!references.TryGetValue(owner,out var values))references.Add(owner,values=[]);values.Add(reader.GetString(1));}
        using(var projected=Command(connection,transaction,"""
            SELECT node_id,execution_id,parent_node_id,kind,trace_id,span_id,name_state,name_text,lifecycle,status,
              time_authority,start_utc_ticks,end_utc_ticks,duration_ms,skill_activity_state,skill_activity_count,
              tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,
              error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,token_authority,token_state,
              input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens
            FROM local_workspace_nodes
            WHERE session_id=$session_id AND source_kind NOT IN ('execution_root','session_event') ORDER BY node_id LIMIT 4097;
            """,sessionId))using(var reader=await projected.ExecuteReaderAsync(token))while(await reader.ReadAsync(token))
        {
            var owner=reader.GetString(0);string? span=null;if(!reader.IsDBNull(4)&&!reader.IsDBNull(5))
                spansByIdentity.TryGetValue((reader.GetString(4),reader.GetString(5)),out span);
            nodes.Add(new(owner,reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),
                references.TryGetValue(owner,out var values)?values:[],System.Text.Json.JsonSerializer.SerializeToElement(new{kind=reader.GetString(3),
                    name_state=reader.GetString(6),name=reader.IsDBNull(7)?null:reader.GetString(7),lifecycle=reader.GetString(8),status=reader.GetString(9),
                    time_authority=reader.GetString(10),start_utc_ticks=reader.IsDBNull(11)?(long?)null:reader.GetInt64(11),
                    end_utc_ticks=reader.IsDBNull(12)?(long?)null:reader.GetInt64(12),duration_ms=reader.IsDBNull(13)?(long?)null:reader.GetInt64(13),
                    skill_activity=new{state=reader.GetString(14),count=reader.IsDBNull(15)?(long?)null:reader.GetInt64(15)},
                    tool_activity=new{state=reader.GetString(16),count=reader.IsDBNull(17)?(long?)null:reader.GetInt64(17)},
                    subagent_activity=new{state=reader.GetString(18),count=reader.IsDBNull(19)?(long?)null:reader.GetInt64(19)},
                    error_activity=new{state=reader.GetString(20),count=reader.IsDBNull(21)?(long?)null:reader.GetInt64(21)},
                    retry_activity=new{state=reader.GetString(22),count=reader.IsDBNull(23)?(long?)null:reader.GetInt64(23)},
                    token_authority=reader.GetString(24),token_state=reader.GetString(25),input_tokens=reader.IsDBNull(26)?(long?)null:reader.GetInt64(26),
                    output_tokens=reader.IsDBNull(27)?(long?)null:reader.GetInt64(27),total_tokens=reader.IsDBNull(28)?(long?)null:reader.GetInt64(28),
                    reasoning_tokens=reader.IsDBNull(29)?(long?)null:reader.GetInt64(29),cache_read_tokens=reader.IsDBNull(30)?(long?)null:reader.GetInt64(30),
                    cache_creation_tokens=reader.IsDBNull(31)?(long?)null:reader.GetInt64(31)}),span));
        }
        foreach(var root in nodes.Where(node=>node.ParentNodeId is null&&spansByExecution.ContainsKey(node.ExecutionId)).ToArray())
        {
            var index=nodes.IndexOf(root);if(index>=0)nodes[index]=root with{SanitizedSpanObservations=spansByExecution[root.ExecutionId]};
        }
        var rawEvidence=new List<LocalAiRawEvidenceV1>();
        using (var content=Command(connection,transaction,"""
            SELECT c.node_id,c.part,c.availability_state,c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,
              c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,
              c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token
            FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id
            WHERE n.session_id=$session_id ORDER BY c.node_id,c.part;
            """,sessionId))
        using(var reader=await content.ExecuteReaderAsync(token))while(await reader.ReadAsync(token))
        {
            var owner=reader.GetString(0);var part=reader.GetString(1);
            rawEvidence.Add(new($"raw:{owner}:{part}",owner,new(owner,part,reader.GetString(2),
                reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),
                reader.IsDBNull(6)?null:reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetInt64(8),
                reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),
                reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetInt64(13),reader.IsDBNull(14)?null:(byte[])reader[14],reader.IsDBNull(15)?null:(byte[])reader[15])));
        }
        System.Text.Json.JsonElement? sessionFacts=null;
        using(var session=Command(connection,transaction,"""
            SELECT status,completeness,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions WHERE session_id=$session_id;
            """,sessionId))using(var reader=await session.ExecuteReaderAsync(token))if(await reader.ReadAsync(token))
            sessionFacts=System.Text.Json.JsonSerializer.SerializeToElement(new{status=reader.GetString(0),completeness=reader.GetString(1),
                started_at=reader.IsDBNull(2)?null:reader.GetString(2),ended_at=reader.IsDBNull(3)?null:reader.GetString(3),
                last_seen_at=reader.GetString(4),raw_retention_state=reader.GetString(5),created_at=reader.GetString(6),updated_at=reader.GetString(7)});
        var input=new LocalAiProjectionInputV1(sessionId,revision,executions,nodes,spans,nodeId,rawEvidence,events.Count,sessionFacts);
        return new(input,pinnedRegistry.CanonicalIdentity);
    }

    public ValueTask<LocalWorkspaceComparisonDetailContribution> ReadComparisonPinnedAsync(
        ILocalRepositoryReadTransaction transaction, string sessionId, DateTimeOffset acceptedAt,
        PinnedRegistryAuthority pinnedRegistry, CancellationToken cancellationToken) =>
        transaction.ReadAsync<LocalWorkspaceComparisonDetailContribution>(async (connection, sqliteTransaction, token) =>
        {
            var detail = await ReadAsync(connection, sqliteTransaction,
                new(LocalRepositorySessionDetailRequestKind.Compare, sessionId, Limit: MaximumNodes),
                acceptedAt, pinnedRegistry, revisionSession: null, token);
            var versions = await ReadComparisonVersions(connection, sqliteTransaction, sessionId, token);
            return new(detail.Nodes, versions.SourceApplicationVersions, versions.AdapterVersions,
                detail.CanonicalRevisionInput!, detail.SkillRegistryGenerationIdentity!);
        }, cancellationToken);

    private async ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, LocalRepositorySessionDetailRequest request,
        DateTimeOffset acceptedAt, PinnedRegistryAuthority pinnedRegistry,
        LocalRepositoryScopeSessionSnapshot? revisionSession, CancellationToken token)
    {
        var sessionId = request.SessionId;
        await ValidateSourceOwnerBounds(connection, transaction, sessionId, token);
        var currentSkills = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, [sessionId], acceptedAt, pinnedRegistry);
        currentSkills.TryGetValue(sessionId, out var skillProjection);
        if (skillProjection?.State == "unavailable")
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var registryIdentity = pinnedRegistry.CanonicalIdentity;
        var revision = await ReadCanonicalRevisionInput(
            connection, transaction, sessionId, acceptedAt, skillProjection, registryIdentity, token);
        var materializableSkillNodeIds = await ReadMaterializableCurrentSkillNodeIds(
            connection, transaction, sessionId, skillProjection, token);
        await ValidateBounds(connection, transaction, sessionId, materializableSkillNodeIds, token);
        var revisionMatches = request.ExpectedWorkspaceRevision is null || revisionSession is not null && string.Equals(
            request.ExpectedWorkspaceRevision,
            SqliteLocalRepositoryScopeSnapshotService.ComputeRevisionForTest(revisionSession, revision, registryIdentity),
            StringComparison.Ordinal);
        if (!revisionMatches && request.Kind != LocalRepositorySessionDetailRequestKind.Content)
        {
            throw new LocalWorkspaceSessionDetailException("workspace_snapshot_stale");
        }
        await ValidateCurrentSkillOwnerGraph(
            connection, transaction, sessionId, acceptedAt, skillProjection, pinnedRegistry.CanonicalIdentity, token);
        await ValidateSemanticOwnerGraphs(connection, transaction, sessionId, token);
        await ValidateCoreOwnerGraph(connection, transaction, sessionId, token);
        var contentGraphValid = LocalWorkspaceContentAuthority.ValidateSessionGraph(connection, transaction, sessionId, acceptedAt);
        if (!contentGraphValid && request.Kind != LocalRepositorySessionDetailRequestKind.Content)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var syntheticSkillTarget = IsCurrentSkillNode(request.NodeId, skillProjection);
        if (request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content
            && !syntheticSkillTarget)
            await ValidateNodeAncestry(connection, transaction, request, token);
        var projectedNodes = NormalizeCurrentSkillNodes(
            await ReadNodes(connection, transaction, request, skillProjection, token), skillProjection);
        var persistedNodeIds = await ReadPersistedCurrentSkillNodeIds(connection, transaction, sessionId, skillProjection, token);
        projectedNodes = await AddMissingCurrentSkillNodes(
            connection, transaction, request, projectedNodes, skillProjection, materializableSkillNodeIds, persistedNodeIds, token);
        var synthesizedSkillNodes = projectedNodes.Where(node => node.SourceKind == "skill_invocation" && !persistedNodeIds.Contains(node.NodeId)).ToArray();
        var admittedSkillIdentities = skillProjection?.State is "current" or "certification_pending"
            ? skillProjection.Invocations.Select(static invocation => invocation.CanonicalIdentity).ToHashSet(StringComparer.Ordinal)
            : [];
        var excludedSkillNodes = projectedNodes.Where(node => node.SourceKind == "skill_invocation"
            && !admittedSkillIdentities.Contains(node.SourceIdentity)).ToArray();
        var nodes = projectedNodes.Except(excludedSkillNodes).ToArray();
        var executionIds = nodes.Select(static node => node.ExecutionId).Distinct(StringComparer.Ordinal).ToArray();
        var executions = ApplyCurrentSkillActivity(
            await ReadExecutions(connection, transaction, request, executionIds, skillProjection, token), skillProjection, synthesizedSkillNodes);
        nodes = ApplyCurrentSkillActivity(nodes, executions, synthesizedSkillNodes);
        var nodeIds = nodes.Select(static node => node.NodeId).ToArray();
        nodes = await ReadV5NodeFacts(connection, transaction, request, nodes, skillProjection, token);
        nodes = ApplyCurrentSkillMetadata(nodes, skillProjection);
        var edges = request.Kind == LocalRepositorySessionDetailRequestKind.Node
            ? await ReadEdges(connection, transaction, [request.NodeId!], token)
            : [];
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Node && syntheticSkillTarget)
        {
            var selected = nodes.Single(node => node.NodeId == request.NodeId);
            if (selected.ParentNodeId is not null)
                edges = [.. edges, new(selected.NodeId, selected.ParentNodeId, "parent", "exact", selected.SourceOrdinal)];
        }
        var metadata = request.Kind is LocalRepositorySessionDetailRequestKind.Summary or LocalRepositorySessionDetailRequestKind.Compare
            ? await ReadMetadata(connection, transaction, sessionId, token)
            : new Metadata([], [], null, null);
        var content = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? await ReadSummaryContent(connection, transaction, sessionId, acceptedAt, token)
            : request.Kind == LocalRepositorySessionDetailRequestKind.Compare ? []
            : await ReadContent(connection, transaction, nodeIds, acceptedAt, token);
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Content)
        {
            var terminalContent = await ReadTerminalContent(connection, transaction, request.NodeId!, request.ContentPart!, acceptedAt, token);
            if (terminalContent is not null)
                content = [terminalContent];
            var selectedContent = content.SingleOrDefault(item => item.NodeId == request.NodeId && item.Part == request.ContentPart);
            var projectedState = await ReadProjectedContentState(
                connection, transaction, request.NodeId!, request.ContentPart!, token);
            if (contentGraphValid && selectedContent is not null
                && projectedState is "expired" or "deleted" or "read_denied")
            {
                selectedContent = selectedContent with { State = projectedState };
                content = [selectedContent];
            }
            var authoritativeTerminal = selectedContent?.State is "invalid" or "expired" or "deleted" or "read_denied"
                && (string.Equals(selectedContent.State, projectedState, StringComparison.Ordinal)
                    || contentGraphValid && projectedState is "expired" or "deleted" or "read_denied");
            if (!authoritativeTerminal)
            {
                if (!revisionMatches)
                    throw new LocalWorkspaceSessionDetailException("workspace_snapshot_stale");
                if (!contentGraphValid)
                    throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            }
        }
        return new(Array.AsReadOnly(executions), Array.AsReadOnly(nodes), Array.AsReadOnly(edges), Array.AsReadOnly(content),
            metadata.NativeSessionIds, metadata.Versions, metadata.InstructionSourceIdentity, metadata.InstructionAdditionalCount, revision, registryIdentity);
    }

    private static async Task<string?> ReadProjectedContentState(
        SqliteConnection connection, SqliteTransaction transaction, string nodeId, string part, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT availability_state FROM local_workspace_node_content_refs WHERE node_id=$node_id AND part=$part;";
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$part", part);
        return await command.ExecuteScalarAsync(token) as string;
    }

    private async Task<LocalWorkspaceContentAvailability?> ReadTerminalContent(SqliteConnection c, SqliteTransaction t,
        string nodeId, string part, DateTimeOffset acceptedAt, CancellationToken token)
    {
        using var command=c.CreateCommand();command.Transaction=t;
        command.CommandText=$"SELECT c.node_id,c.part,CASE WHEN tombstone.source_item_id=c.source_item_id AND i.state='deleted' AND i.deleted_at=tombstone.deleted_at THEN 'deleted' ELSE {LocalWorkspaceContentAuthority.EffectiveAvailabilitySql} END,c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id LEFT JOIN session_event_content s ON s.event_id=e.event_id LEFT JOIN retention_items i ON i.item_id=c.retention_item_id LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id LEFT JOIN local_workspace_content_tombstones tombstone ON tombstone.store_kind=c.store_kind AND tombstone.source_item_id=c.source_item_id AND tombstone.part=c.part WHERE c.node_id=$node_id AND c.part=$part;";
        command.Parameters.AddWithValue("$now",Canonical(acceptedAt));command.Parameters.AddWithValue("$node_id",nodeId);command.Parameters.AddWithValue("$part",part);
        using var reader=await command.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return null;var value=Content(reader);return value.State is "deleted" or "expired" or "read_denied"?value:null;
    }

    private static bool IsCurrentSkillNode(string? nodeId, SkillProjectionCurrentInvocationProjection? projection) =>
        nodeId is not null && projection?.State is "current" or "certification_pending" && projection.Invocations.Any(invocation =>
            string.Equals(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity), nodeId, StringComparison.Ordinal));

    private static async Task<HashSet<string>> ReadPersistedCurrentSkillNodeIds(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        SkillProjectionCurrentInvocationProjection? projection, CancellationToken token)
    {
        var ids = projection is not null && projection.State is "current" or "certification_pending"
            ? projection.Invocations.Select(static invocation => LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity)).ToArray()
            : [];
        if (ids.Length == 0) return new(StringComparer.Ordinal);
        using var command = Command(connection, transaction,
            "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session_id AND node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));", sessionId);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<HashSet<string>> ReadMaterializableCurrentSkillNodeIds(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        SkillProjectionCurrentInvocationProjection? projection, CancellationToken token)
    {
        var candidates = projection?.State is "current" or "certification_pending"
            ? projection.Invocations.Where(static invocation => invocation.CurrentValidState == "current"
                    && invocation.ExecutionSourceKind is not null && invocation.ExecutionSourceIdentity is not null)
                .Select(invocation => new
                {
                    node_id = LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity),
                    execution_kind = invocation.ExecutionSourceKind,
                    execution_identity = invocation.ExecutionSourceIdentity,
                }).ToArray()
            : [];
        if (candidates.Length == 0) return new(StringComparer.Ordinal);
        using var command = Command(connection, transaction, """
            SELECT DISTINCT value->>'node_id' FROM json_each($candidates)
            JOIN local_workspace_execution_headers h ON h.session_id=$session_id
              AND h.source_kind=value->>'execution_kind' AND h.source_identity=value->>'execution_identity';
            """, sessionId);
        command.Parameters.AddWithValue("$candidates", System.Text.Json.JsonSerializer.Serialize(candidates));
        using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<LocalWorkspaceNodeDetail[]> AddMissingCurrentSkillNodes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        LocalWorkspaceNodeDetail[] nodes,
        SkillProjectionCurrentInvocationProjection? projection,
        IReadOnlySet<string> materializableSkillNodeIds,
        IReadOnlySet<string> persistedSkillNodeIds,
        CancellationToken token)
    {
        if (projection?.State is not ("current" or "certification_pending")) return nodes;
        var existing = nodes.Select(static node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var additions = new List<LocalWorkspaceNodeDetail>();
        var candidates = projection.Invocations
            .Where(static invocation => invocation.CurrentValidState == "current")
            .Where(invocation => invocation.ExecutionSourceKind is not null && invocation.ExecutionSourceIdentity is not null)
            .Where(invocation => materializableSkillNodeIds.Contains(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity)))
            .Where(invocation => !persistedSkillNodeIds.Contains(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity)))
            .Where(invocation => !existing.Contains(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity)))
            .Where(invocation => request.Kind is not (LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content)
                || string.Equals(request.NodeId, LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity), StringComparison.Ordinal))
            .OrderBy(static invocation => invocation.CanonicalIdentity, StringComparer.Ordinal).ToArray();
        var byIdentity = candidates.ToDictionary(static invocation => invocation.CanonicalIdentity, StringComparer.Ordinal);
        using (var command = Command(connection, transaction, """
            WITH canonical AS (
              SELECT value->>'identity' identity,value->>'execution_kind' execution_kind,
                     value->>'execution_identity' execution_identity,value->>'event_id' event_id
              FROM json_each($skills))
            SELECT c.identity,h.execution_id,r.node_id,e.occurred_at,
                   COALESCE((SELECT MAX(n.source_ordinal) FROM local_workspace_nodes n WHERE n.execution_id=h.execution_id),0)+
                     row_number() OVER(PARTITION BY h.execution_id ORDER BY c.identity COLLATE BINARY)
            FROM canonical c
            JOIN local_workspace_execution_headers h ON h.session_id=$session_id AND h.source_kind=c.execution_kind AND h.source_identity=c.execution_identity
            JOIN local_workspace_nodes r ON r.execution_id=h.execution_id AND r.source_kind='execution_root'
            LEFT JOIN session_events e ON e.session_id=$session_id AND e.event_id=c.event_id
            ORDER BY h.execution_id,c.identity COLLATE BINARY;
            """, request.SessionId))
        {
            command.Parameters.AddWithValue("$skills", System.Text.Json.JsonSerializer.Serialize(candidates.Select(invocation => new
            {
                identity = invocation.CanonicalIdentity,
                execution_kind = invocation.ExecutionSourceKind,
                execution_identity = invocation.ExecutionSourceIdentity,
                event_id = invocation.OtelCarrierEventId ?? invocation.SdkCarrierEventId,
            })));
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
            var invocation = byIdentity[reader.GetString(0)];
            var nodeId = LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity);
            var executionId = reader.GetString(1);
            if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline
                && request.ExecutionId is not null && !string.Equals(request.ExecutionId, executionId, StringComparison.Ordinal)) continue;
            if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && request.ParentNodeId is not null
                && !string.Equals(request.ParentNodeId, reader.GetString(2), StringComparison.Ordinal)
                && !string.Equals(request.ParentNodeId, nodeId, StringComparison.Ordinal)) continue;
            var ticks = reader.IsDBNull(3) || !DateTimeOffset.TryParse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var occurredAt)
                ? (long?)null
                : occurredAt.UtcTicks;
            var ordinal = reader.GetInt64(4);
            var none = new LocalWorkspaceFact<long>("not_observed", null);
            var activity = new LocalWorkspaceActivityFacts(new("recorded", 1), none, none, none, none);
            var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 1, none, none, none, none, none, none, none, none);
            additions.Add(new(nodeId, request.SessionId, executionId, "skill_invocation", invocation.CanonicalIdentity, ordinal,
                reader.GetString(2), "exact", "skill", "recorded", invocation.SdkSkillName ?? invocation.OtelSkillName,
                "completed", "completed", ticks is null ? "missing" : "recorded", ticks, ticks, ticks is null ? null : 0,
                activity, tokens, invocation.ProducerTraceId, invocation.ProducerSpanId,
                invocation.SdkCarrierEventId ?? invocation.OtelCarrierEventId));
            }
        }
        if (additions.Count == 0) return nodes;
        if (request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content)
        {
            var parentIds = additions.Select(static node => node.ParentNodeId).Where(static id => id is not null).ToHashSet(StringComparer.Ordinal);
            using var command = Command(connection, transaction, "SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,trace_id,span_id,event_id,0 FROM local_workspace_nodes WHERE session_id=$session_id AND node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));", request.SessionId);
            command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(parentIds));
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!existing.Contains(reader.GetString(0)))
                    nodes = [.. nodes, new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49),0)];
            }
        }
        return [.. nodes, .. additions];
    }

    private static LocalWorkspaceNodeDetail[] NormalizeCurrentSkillNodes(
        LocalWorkspaceNodeDetail[] nodes,
        SkillProjectionCurrentInvocationProjection? projection)
    {
        if (projection?.State is not ("current" or "certification_pending")) return nodes;
        var current = projection.Invocations.ToDictionary(static invocation => invocation.CanonicalIdentity, StringComparer.Ordinal);
        return nodes.Select(node =>
        {
            if (node.SourceKind != "skill_invocation" || !current.TryGetValue(node.SourceIdentity, out var invocation))
                return node;
            return node with
            {
                NameState = string.IsNullOrWhiteSpace(invocation.SdkSkillName ?? invocation.OtelSkillName) ? "invalid" : "recorded",
                NameText = invocation.SdkSkillName ?? invocation.OtelSkillName,
                TraceId = invocation.ProducerTraceId,
                SpanId = invocation.ProducerSpanId,
                EventId = invocation.SdkCarrierEventId ?? invocation.OtelCarrierEventId,
            };
        }).ToArray();
    }

    private static LocalWorkspaceExecutionDetail[] ApplyCurrentSkillActivity(
        LocalWorkspaceExecutionDetail[] executions,
        SkillProjectionCurrentInvocationProjection? projection,
        LocalWorkspaceNodeDetail[] synthesized)
    {
        return executions.Select(execution =>
        {
            var scoped = projection?.Invocations.Where(invocation =>
                    string.Equals(invocation.ExecutionSourceKind, execution.SourceKind, StringComparison.Ordinal)
                    && string.Equals(invocation.ExecutionSourceIdentity, execution.SourceIdentity, StringComparison.Ordinal)).ToArray() ?? [];
            var currentCount = scoped.LongCount(static invocation => invocation.CurrentValidState == "current");
            var skill = scoped.Any(static invocation => invocation.CurrentValidState == "certification_pending")
                ? new LocalWorkspaceFact<long>("certification_pending", null)
                : currentCount > 0
                    ? new LocalWorkspaceFact<long>("recorded", currentCount)
                    : new LocalWorkspaceFact<long>("not_observed", null);
            return execution with
            {
                Activity = execution.Activity with { Skill = skill },
                ChildCount = execution.ChildCount + synthesized.LongCount(node => node.ExecutionId == execution.ExecutionId),
            };
        }).ToArray();
    }

    private static LocalWorkspaceNodeDetail[] ApplyCurrentSkillActivity(
        LocalWorkspaceNodeDetail[] nodes,
        LocalWorkspaceExecutionDetail[] executions,
        LocalWorkspaceNodeDetail[] synthesized)
    {
        var synthesizedByParent = synthesized.Where(static node => node.ParentNodeId is not null)
            .GroupBy(static node => node.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.LongCount(), StringComparer.Ordinal);
        var byExecution = executions.ToDictionary(static execution => execution.ExecutionId, StringComparer.Ordinal);
        return nodes.Select(node =>
        {
            var activity = byExecution.TryGetValue(node.ExecutionId, out var execution)
                && node.SourceKind == "execution_root"
                    ? node.Activity with { Skill = execution.Activity.Skill }
                    : node.Activity;
            var childCount = node.ChildCount;
            if (synthesizedByParent.TryGetValue(node.NodeId, out var added)) childCount += added;
            return node with { Activity = activity, ChildCount = childCount };
        }).ToArray();
    }

    private static async Task ValidateNodeAncestry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        CancellationToken token)
    {
        using var command = Command(connection, transaction, """
            WITH RECURSIVE ancestry(node_id,parent_node_id,execution_id,depth,path,cycle,cross_scope) AS (
              SELECT node_id,parent_node_id,execution_id,0,char(0)||node_id||char(0),0,0
              FROM local_workspace_nodes WHERE session_id=$session_id AND node_id=$node_id
              UNION ALL
              SELECT parent.node_id,parent.parent_node_id,parent.execution_id,ancestry.depth+1,
                ancestry.path||parent.node_id||char(0),
                instr(ancestry.path,char(0)||parent.node_id||char(0))>0,
                ancestry.cross_scope OR parent.session_id<>$session_id OR parent.execution_id<>ancestry.execution_id
              FROM ancestry JOIN local_workspace_nodes parent ON parent.node_id=ancestry.parent_node_id
              WHERE ancestry.parent_node_id IS NOT NULL AND ancestry.depth<4097 AND ancestry.cycle=0
            )
            SELECT COALESCE(MAX(depth),0),COALESCE(MAX(cycle),0),COALESCE(MAX(cross_scope),0) FROM ancestry;
            """, request.SessionId);
        command.Parameters.AddWithValue("$node_id", request.NodeId!);
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)
            || reader.GetInt64(0) >= 4097
            || reader.GetInt64(1) != 0
            || reader.GetInt64(2) != 0)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
    }

    private static async Task ValidateBounds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        IReadOnlySet<string> materializableSkillNodeIds,
        CancellationToken token)
    {
        using var command = Command(connection, transaction, BoundsSql, sessionId);
        long executionOverflow;
        long nodeOverflow;
        long sourceOverflow;
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            executionOverflow = reader.GetInt64(0);
            nodeOverflow = reader.GetInt64(1);
            sourceOverflow = reader.GetInt64(2);
        }
        var identities = materializableSkillNodeIds.ToArray();
        if (identities.Length != 0)
        {
            using var effective = Command(connection, transaction, """
                SELECT (SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session_id)+
                       (SELECT COUNT(*) FROM json_each($identities) j WHERE NOT EXISTS(
                          SELECT 1 FROM local_workspace_nodes n WHERE n.session_id=$session_id AND n.node_id=CAST(j.value AS TEXT)));
                """, sessionId);
            effective.Parameters.AddWithValue("$identities", System.Text.Json.JsonSerializer.Serialize(identities));
            if (Convert.ToInt64(await effective.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) > MaximumNodes)
                nodeOverflow = 1;
        }
        if (executionOverflow != 0 || nodeOverflow != 0 || sourceOverflow != 0)
            throw new LocalWorkspaceSessionDetailException("workspace_too_large");
    }

    private static async Task ValidateSourceOwnerBounds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken token)
    {
        var skillOwnerQueries = new List<string>();
        if (TableExists(connection, transaction, "skill_projection_invocations"))
            skillOwnerQueries.Add("SELECT invocation_id source_identity FROM skill_projection_invocations WHERE session_id=$session_id");
        if (TableExists(connection, transaction, "skill_invocation_snapshots"))
            skillOwnerQueries.Add("SELECT snapshot_id FROM skill_invocation_snapshots WHERE session_id=$session_id");
        if (TableExists(connection, transaction, "skill_projection_sdk_claims"))
            skillOwnerQueries.Add("SELECT claim_id FROM skill_projection_sdk_claims WHERE session_id=$session_id");
        var skillOwnerOverflow = skillOwnerQueries.Count == 0
            ? "0"
            : $"EXISTS(SELECT 1 FROM ({string.Join(" UNION ALL ", skillOwnerQueries)}) LIMIT 1 OFFSET 4096)";
        var tokenOverflow = TableExists(connection, transaction, "local_workspace_token_observations")
            ? "EXISTS(SELECT 1 FROM local_workspace_token_observations WHERE session_id=$session_id LIMIT 1 OFFSET 4096)"
            : "0";
        var monitorSpanOverflow = HasOtelToolOwnerSchema(connection, transaction)
            ? """
              EXISTS(SELECT 1 FROM monitor_spans span WHERE EXISTS(
                SELECT 1 FROM session_events event WHERE event.session_id=$session_id
                  AND event.source_adapter='otel-exact' COLLATE BINARY
                  AND event.type='otel.span' COLLATE BINARY
                  AND event.trace_id=span.trace_id COLLATE BINARY
                  AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY)
                LIMIT 1 OFFSET 4096)
              """
            : "0";
        const string semanticReferenceOverflow = """
          EXISTS(
            SELECT 1 FROM session_events start
            JOIN session_runs run ON run.session_id=start.session_id AND run.run_id=start.run_id
            WHERE start.session_id=$session_id AND start.source_surface='copilot-sdk' COLLATE BINARY
              AND start.source_adapter='copilot-sdk-stream' COLLATE BINARY AND start.type='tool.execution_start'
              AND start.source_event_id IS NOT NULL AND length(start.source_event_id)>0
              AND run.source_surface='copilot-sdk' COLLATE BINARY AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
              AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=start.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
              AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=start.session_id
                AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
              AND EXISTS(SELECT 1 FROM session_events completion
                WHERE completion.session_id=start.session_id AND completion.run_id=start.run_id
                  AND completion.source_surface=start.source_surface COLLATE BINARY
                  AND completion.source_adapter=start.source_adapter COLLATE BINARY
                  AND completion.type='tool.execution_complete' AND completion.parent_event_id=start.event_id
                LIMIT 1 OFFSET 15))
          OR EXISTS(
            SELECT 1 FROM session_events event
            JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
            WHERE event.session_id=$session_id AND event.source_surface='copilot-sdk' COLLATE BINARY
              AND event.source_adapter='copilot-sdk-stream' COLLATE BINARY
              AND event.type IN ('subagent.selected','subagent.started','subagent.completed','subagent.failed','subagent.deselected')
              AND run.source_surface='copilot-sdk' COLLATE BINARY AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
              AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=event.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
              AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=event.session_id
                AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
            GROUP BY event.run_id HAVING COUNT(DISTINCT event.event_id)>16)
          """;
        using var command = Command(connection, transaction, $"""
            SELECT
              EXISTS(SELECT 1 FROM session_runs WHERE session_id=$session_id LIMIT 1 OFFSET 256),
              EXISTS(SELECT 1 FROM (
                SELECT run_id source_identity FROM session_runs WHERE session_id=$session_id
                UNION ALL
                SELECT event_id FROM session_events WHERE session_id=$session_id AND run_id IS NOT NULL)
                LIMIT 1 OFFSET 4096),
              EXISTS(SELECT 1 FROM session_native_ids WHERE session_id=$session_id LIMIT 1 OFFSET 4096),
              {skillOwnerOverflow},
              {tokenOverflow},
              {monitorSpanOverflow},
              {semanticReferenceOverflow},
              EXISTS(SELECT 1 FROM local_workspace_nodes node WHERE node.session_id=$session_id AND (
                EXISTS(SELECT 1 FROM local_workspace_node_edges edge
                  WHERE edge.node_id=node.node_id AND edge.relation_kind='retry' LIMIT 1 OFFSET 200)
                OR EXISTS(SELECT 1 FROM local_workspace_node_edges edge
                  WHERE edge.node_id=node.node_id AND edge.relation_kind='recovery' LIMIT 1 OFFSET 200)));
            """, sessionId);
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0
            || reader.GetInt64(2) != 0 || reader.GetInt64(3) != 0
            || reader.GetInt64(4) != 0 || reader.GetInt64(5) != 0
            || reader.GetInt64(6) != 0 || reader.GetInt64(7) != 0)
            throw new LocalWorkspaceSessionDetailException("workspace_too_large");
    }

    private static async Task ValidateSemanticOwnerGraphs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken token)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        if (await SemanticGraphInvalid(connection, transaction, sessionId, SemanticReceiptCoverageSql, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var hasOtelOwnerSchema = HasOtelToolOwnerSchema(connection, transaction);
        if (hasOtelOwnerSchema
                ? await SemanticGraphInvalid(connection, transaction, sessionId, OtelToolOwnerValidationSql, token)
                : await RequiresOtelToolOwnerValidation(connection, transaction, sessionId, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (await SemanticGraphInvalid(connection, transaction, sessionId, SdkToolOwnerValidationSql, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (await SemanticGraphInvalid(connection, transaction, sessionId, SdkSubagentOwnerValidationSql, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (await SemanticGraphInvalid(connection, transaction, sessionId, ClaudeHookOwnerValidationSql, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
    }

    private static async Task ValidateCoreOwnerGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken token)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        var executionFactsInvalid = await SemanticGraphInvalid(
            connection, transaction, sessionId, ExecutionFactsValidationSql, token);
        var executionRetryFactsInvalid = await SemanticGraphInvalid(
            connection,
            transaction,
            sessionId,
            HasOtelToolOwnerSchema(connection, transaction)
                ? ExecutionRetryFactsValidationSql
                : ExecutionRetryFactsAbsentValidationSql,
            token);
        if (executionFactsInvalid || executionRetryFactsInvalid
            || await SemanticGraphInvalid(connection, transaction, sessionId, NonParentEdgeValidationSql, token)
            || await SemanticGraphInvalid(connection, transaction, sessionId, MetadataKindValidationSql, token)
            || await SemanticGraphInvalid(connection, transaction, sessionId, CoreOwnerValidationSql, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
    }

    private const string NonParentEdgeValidationSql = """
        SELECT EXISTS(
          SELECT 1 FROM local_workspace_node_edges edge
          JOIN local_workspace_nodes node ON node.node_id=edge.node_id
          WHERE node.session_id=$session_id AND edge.relation_kind<>'parent');
        """;

    private const string MetadataKindValidationSql = """
        SELECT EXISTS(
          SELECT 1 FROM local_workspace_tool_metadata metadata
          JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
          WHERE node.session_id=$session_id AND (node.source_kind<>'semantic_tool' OR node.kind<>'tool')
          UNION ALL
          SELECT 1 FROM local_workspace_subagent_lifecycle lifecycle
          JOIN local_workspace_nodes node ON node.node_id=lifecycle.node_id
          WHERE node.session_id=$session_id AND (node.source_kind<>'semantic_subagent' OR node.kind<>'subagent')
          UNION ALL
          SELECT 1 FROM local_workspace_skill_metadata metadata
          JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
          WHERE node.session_id=$session_id AND (node.source_kind<>'skill_invocation' OR node.kind<>'skill'));
        """;

    private static async Task<bool> RequiresOtelToolOwnerValidation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken token)
    {
        using var command = Command(connection, transaction, """
            SELECT EXISTS(
              SELECT 1 FROM session_events WHERE session_id=$session_id
                AND source_adapter='otel-exact' COLLATE BINARY AND type='otel.span' COLLATE BINARY
              UNION ALL
              SELECT 1 FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE node.session_id=$session_id AND receipt.semantic_kind='tool' AND receipt.source_family='otel');
            """, sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasOtelToolOwnerSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)=13 FROM pragma_table_info('monitor_spans')
            WHERE name IN ('raw_record_id','span_ordinal','trace_id','span_id','parent_span_id','operation','category',
                           'tool_name','mcp_tool_name','mcp_server_hash','status','start_time','end_time');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private const string ExecutionFactsValidationSql = """
        WITH ranked_tokens AS (
          SELECT observation.*,
                 row_number() OVER(PARTITION BY observation.session_id,observation.execution_id ORDER BY
                   observation.authority_rank,observation.source_identity COLLATE BINARY) ordinal
          FROM local_workspace_token_observations observation
          WHERE observation.session_id=$session_id
        ),
        chosen_tokens AS (SELECT * FROM ranked_tokens WHERE ordinal=1),
        event_facts AS (
          SELECT header.execution_id,
                 COUNT(DISTINCT CASE WHEN
                   (event.type='tool.execution_start' AND event.source_adapter='copilot-sdk-stream'
                     AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>'')
                   OR (event.type='PreToolUse' AND event.source_surface='claude-code'
                     AND event.source_adapter='claude-code-hook' AND event.source_event_id IS NOT NULL
                     AND trim(event.source_event_id)<>'' AND event.adapter_version IS NOT NULL
                     AND trim(event.adapter_version)<>'' AND event.normalization_version IS NOT NULL
                     AND trim(event.normalization_version)<>''
                     AND ((event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>'')
                       OR (length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                         AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                   THEN event.event_id END) tool_count,
                 COUNT(DISTINCT CASE WHEN
                   (event.type='subagent.started' AND event.source_adapter='copilot-sdk-stream'
                     AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>'')
                   OR (event.type='SubagentStart' AND event.source_surface='claude-code'
                     AND event.source_adapter='claude-code-hook' AND event.source_event_id IS NOT NULL
                     AND trim(event.source_event_id)<>'' AND event.adapter_version IS NOT NULL
                     AND trim(event.adapter_version)<>'' AND event.normalization_version IS NOT NULL
                     AND trim(event.normalization_version)<>''
                     AND ((event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>'')
                       OR (length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                         AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                   THEN event.event_id END) subagent_count,
                 COUNT(DISTINCT CASE WHEN event.type IN ('PostToolUseFailure','StopFailure','subagent.failed')
                   OR event.terminal_outcome='failed' THEN event.event_id END) error_count
          FROM local_workspace_execution_headers header
          LEFT JOIN session_events event ON event.session_id=header.session_id
            AND event.run_id=header.source_identity COLLATE BINARY
          WHERE header.session_id=$session_id
          GROUP BY header.execution_id
        ),
        otel_tool_facts AS (
          SELECT node.execution_id,COUNT(*) tool_count
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          JOIN local_workspace_execution_headers header ON header.execution_id=node.execution_id
            AND header.session_id=node.session_id
          WHERE node.session_id=$session_id AND receipt.source_family='otel' AND receipt.semantic_kind='tool'
          GROUP BY node.execution_id
        )
        SELECT EXISTS(
          SELECT 1
          FROM local_workspace_execution_headers header
          JOIN event_facts event ON event.execution_id=header.execution_id
          LEFT JOIN otel_tool_facts otel_tool ON otel_tool.execution_id=header.execution_id
          LEFT JOIN chosen_tokens token ON token.session_id=header.session_id
            AND token.execution_id=header.source_identity COLLATE BINARY
          WHERE header.session_id=$session_id AND (
            header.tool_activity_state<>CASE WHEN event.tool_count=0 AND otel_tool.tool_count IS NULL THEN 'not_observed' ELSE 'recorded' END
            OR header.tool_activity_count IS NOT CASE WHEN event.tool_count=0 THEN otel_tool.tool_count ELSE event.tool_count END
            OR header.subagent_activity_state<>CASE WHEN event.subagent_count=0 THEN 'not_observed' ELSE 'recorded' END
            OR header.subagent_activity_count IS NOT CASE WHEN event.subagent_count=0 THEN NULL ELSE event.subagent_count END
            OR header.error_activity_state<>CASE WHEN event.error_count=0 THEN 'not_observed' ELSE 'recorded' END
            OR header.error_activity_count IS NOT CASE WHEN event.error_count=0 THEN NULL ELSE event.error_count END
            OR header.token_authority<>CASE
              WHEN token.input_tokens IS NULL AND token.output_tokens IS NULL AND token.total_tokens IS NULL
                AND token.reasoning_tokens IS NULL AND token.cache_read_tokens IS NULL AND token.cache_creation_tokens IS NULL THEN 'none'
              WHEN (token.total_tokens IS NOT NULL AND token.input_tokens IS NOT NULL AND token.output_tokens IS NOT NULL
                    AND token.total_tokens<>token.input_tokens+token.output_tokens)
                OR (token.cache_read_tokens IS NOT NULL
                    AND (token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens)) THEN 'none'
              ELSE token.authority END
            OR header.token_state<>CASE
              WHEN token.input_tokens IS NULL AND token.output_tokens IS NULL AND token.total_tokens IS NULL
                AND token.reasoning_tokens IS NULL AND token.cache_read_tokens IS NULL AND token.cache_creation_tokens IS NULL THEN 'not_observed'
              WHEN (token.total_tokens IS NOT NULL AND token.input_tokens IS NOT NULL AND token.output_tokens IS NOT NULL
                    AND token.total_tokens<>token.input_tokens+token.output_tokens)
                OR (token.cache_read_tokens IS NOT NULL
                    AND (token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens)) THEN 'inconsistent'
              ELSE 'recorded' END
            OR header.available_execution_count<>CASE
              WHEN token.input_tokens IS NULL AND token.output_tokens IS NULL AND token.total_tokens IS NULL
                AND token.reasoning_tokens IS NULL AND token.cache_read_tokens IS NULL AND token.cache_creation_tokens IS NULL THEN 0
              WHEN (token.total_tokens IS NOT NULL AND token.input_tokens IS NOT NULL AND token.output_tokens IS NOT NULL
                    AND token.total_tokens<>token.input_tokens+token.output_tokens)
                OR (token.cache_read_tokens IS NOT NULL
                    AND (token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens)) THEN 0
              ELSE 1 END
            OR header.total_execution_count<>1
            OR header.input_token_state<>CASE WHEN token.input_tokens IS NULL THEN 'not_observed'
              WHEN token.cache_read_tokens IS NOT NULL AND token.cache_read_tokens>token.input_tokens THEN 'inconsistent'
              ELSE 'recorded' END
            OR header.input_tokens IS NOT CASE WHEN token.input_tokens IS NOT NULL
              AND NOT (token.cache_read_tokens IS NOT NULL AND token.cache_read_tokens>token.input_tokens)
              THEN token.input_tokens END
            OR header.output_token_state<>CASE WHEN token.output_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END
            OR header.output_tokens IS NOT token.output_tokens
            OR header.total_token_state<>CASE WHEN token.total_tokens IS NULL THEN 'not_observed'
              WHEN token.input_tokens IS NOT NULL AND token.output_tokens IS NOT NULL
                AND token.total_tokens<>token.input_tokens+token.output_tokens THEN 'inconsistent' ELSE 'recorded' END
            OR header.total_tokens IS NOT CASE WHEN token.total_tokens IS NOT NULL
              AND NOT (token.input_tokens IS NOT NULL AND token.output_tokens IS NOT NULL
                AND token.total_tokens<>token.input_tokens+token.output_tokens) THEN token.total_tokens END
            OR header.reasoning_token_state<>CASE WHEN token.reasoning_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END
            OR header.reasoning_tokens IS NOT token.reasoning_tokens
            OR header.cache_read_token_state<>CASE WHEN token.cache_read_tokens IS NULL THEN 'not_observed'
              WHEN token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens THEN 'inconsistent' ELSE 'recorded' END
            OR header.cache_read_tokens IS NOT CASE WHEN token.cache_read_tokens IS NOT NULL
              AND token.input_tokens IS NOT NULL AND token.cache_read_tokens<=token.input_tokens THEN token.cache_read_tokens END
            OR header.cache_creation_token_state<>CASE WHEN token.cache_creation_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END
            OR header.cache_creation_tokens IS NOT token.cache_creation_tokens
            OR header.new_input_token_state<>CASE
              WHEN token.cache_read_tokens IS NOT NULL
                AND (token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens) THEN 'inconsistent'
              WHEN token.input_tokens IS NOT NULL AND token.cache_read_tokens IS NOT NULL THEN 'recorded'
              ELSE 'not_observed' END
            OR header.new_input_tokens IS NOT CASE WHEN token.input_tokens IS NOT NULL
              AND token.cache_read_tokens IS NOT NULL AND token.cache_read_tokens<=token.input_tokens
              THEN token.input_tokens-token.cache_read_tokens END
            OR header.cache_read_ratio_state<>CASE
              WHEN token.cache_read_tokens IS NOT NULL
                AND (token.input_tokens IS NULL OR token.cache_read_tokens>token.input_tokens) THEN 'inconsistent'
              WHEN token.input_tokens>0 AND token.cache_read_tokens IS NOT NULL THEN 'recorded'
              ELSE 'not_observed' END
            OR header.cache_read_ratio_basis_points IS NOT CASE WHEN token.input_tokens>0
              AND token.cache_read_tokens IS NOT NULL AND token.cache_read_tokens<=token.input_tokens
              THEN (token.cache_read_tokens*10000)/token.input_tokens END));
        """;

    private const string ExecutionRetryFactsValidationSql = """
        WITH exact_spans AS (
          SELECT DISTINCT event.session_id,event.run_id,span.raw_record_id,span.span_ordinal,fact.retry_count
          FROM session_events event
          JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
            AND event.type='otel.span' COLLATE BINARY
            AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
            AND event.trace_id=span.trace_id COLLATE BINARY
          JOIN local_workspace_span_facts fact
            ON fact.raw_record_id=span.raw_record_id AND fact.span_ordinal=span.span_ordinal
          WHERE event.session_id=$session_id AND event.run_id IS NOT NULL
            AND span.operation='chat' COLLATE BINARY AND fact.retry_count IS NOT NULL
            AND (SELECT COUNT(*) FROM monitor_spans owner
              WHERE lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                AND lower(owner.span_id)=lower(span.span_id) COLLATE BINARY)=1
            AND (SELECT COUNT(*) FROM session_events owner
              WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                AND owner.type='otel.span' COLLATE BINARY
                AND lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                AND lower(owner.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)=1
        ),
        totals AS (SELECT session_id,run_id,SUM(retry_count) retry_count FROM exact_spans GROUP BY session_id,run_id)
        SELECT EXISTS(
          SELECT 1 FROM local_workspace_execution_headers header
          LEFT JOIN totals total ON total.session_id=header.session_id
            AND total.run_id=header.source_identity COLLATE BINARY
          WHERE header.session_id=$session_id AND (
            header.retry_activity_state<>CASE WHEN total.retry_count IS NULL THEN 'not_observed' ELSE 'recorded' END
            OR header.retry_activity_count IS NOT total.retry_count));
        """;

    private const string ExecutionRetryFactsAbsentValidationSql = """
        SELECT EXISTS(SELECT 1 FROM local_workspace_execution_headers
          WHERE session_id=$session_id AND (retry_activity_state<>'not_observed' OR retry_activity_count IS NOT NULL));
        """;

    private const string CoreOwnerValidationSql = """
        WITH ranked_runs AS (
          SELECT run.*,
                 row_number() OVER(PARTITION BY run.session_id ORDER BY run.run_id COLLATE BINARY)-1 expected_ordinal,
                 CASE run.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END expected_lifecycle,
                 CASE run.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END expected_status,
                 CASE WHEN run.status IN ('completed','failed') THEN
                        CASE WHEN local_workspace_ticks(run.started_at) IS NOT NULL
                                AND local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at) THEN 'recorded' ELSE 'invalid' END
                      WHEN run.status='active' THEN CASE WHEN local_workspace_ticks(run.started_at) IS NOT NULL AND run.ended_at IS NULL THEN 'recorded' WHEN run.started_at IS NULL AND run.ended_at IS NULL THEN 'missing' ELSE 'invalid' END
                      WHEN local_workspace_ticks(run.started_at) IS NOT NULL AND (run.ended_at IS NULL OR local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at)) THEN 'recorded'
                      WHEN run.started_at IS NULL AND run.ended_at IS NULL THEN 'missing' ELSE 'invalid' END expected_time_authority,
                 CASE WHEN (run.status='active' AND local_workspace_ticks(run.started_at) IS NOT NULL AND run.ended_at IS NULL)
                           OR (run.status IN ('completed','failed') AND local_workspace_ticks(run.started_at) IS NOT NULL AND local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at))
                           OR (run.status NOT IN ('active','completed','failed') AND local_workspace_ticks(run.started_at) IS NOT NULL AND (run.ended_at IS NULL OR local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at)))
                      THEN local_workspace_ticks(run.started_at) END expected_start_ticks,
                 CASE WHEN run.status<>'active' AND local_workspace_ticks(run.started_at) IS NOT NULL
                        AND local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at)
                      THEN local_workspace_ticks(run.ended_at) END expected_end_ticks,
                 CASE WHEN run.status<>'active' AND local_workspace_ticks(run.started_at) IS NOT NULL
                        AND local_workspace_ticks(run.ended_at)>=local_workspace_ticks(run.started_at)
                      THEN (local_workspace_ticks(run.ended_at)-local_workspace_ticks(run.started_at))/10000 END expected_duration,
                 CASE WHEN length(run.trace_id)=32 AND run.trace_id NOT GLOB '*[^0-9a-f]*' THEN run.trace_id END expected_trace
          FROM session_runs run WHERE run.session_id=$session_id
        ),
        invalid_headers AS (
          SELECT 1 FROM local_workspace_execution_headers header
          LEFT JOIN ranked_runs owner ON owner.run_id=header.source_identity COLLATE BINARY
          WHERE header.session_id=$session_id AND (
            owner.run_id IS NULL OR header.source_kind<>'session_run'
            OR header.execution_id<>local_workspace_execution_id('session_run',owner.run_id)
            OR header.source_ordinal<>owner.expected_ordinal
            OR header.lifecycle<>owner.expected_lifecycle OR header.status<>owner.expected_status
            OR header.model IS NOT owner.model OR header.trace_id IS NOT owner.expected_trace
            OR header.time_authority<>owner.expected_time_authority
            OR header.start_utc_ticks IS NOT owner.expected_start_ticks
            OR header.end_utc_ticks IS NOT owner.expected_end_ticks
            OR header.duration_ms IS NOT owner.expected_duration)
          UNION ALL
          SELECT 1 FROM ranked_runs owner WHERE NOT EXISTS(
            SELECT 1 FROM local_workspace_execution_headers header
            WHERE header.session_id=owner.session_id AND header.source_kind='session_run'
              AND header.source_identity=owner.run_id COLLATE BINARY)
        ),
        invalid_roots AS (
          SELECT 1 FROM local_workspace_nodes node
          LEFT JOIN local_workspace_execution_headers header
            ON header.execution_id=node.execution_id AND header.session_id=node.session_id
          WHERE node.session_id=$session_id AND node.source_kind='execution_root' AND (
            header.execution_id IS NULL OR node.node_id<>local_workspace_node_id('execution_root',header.source_identity)
            OR node.source_identity<>header.source_identity OR node.source_ordinal<>0
            OR node.parent_node_id IS NOT NULL OR node.relationship_authority<>'exact' OR node.kind<>'execution'
            OR node.name_state<>'not_observed' OR node.name_text IS NOT NULL
            OR node.lifecycle<>CASE header.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END
            OR node.status<>CASE header.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END
            OR node.time_authority<>header.time_authority OR node.start_utc_ticks IS NOT header.start_utc_ticks
            OR node.end_utc_ticks IS NOT header.end_utc_ticks OR node.duration_ms IS NOT header.duration_ms
            OR node.trace_id IS NOT NULL OR node.span_id IS NOT NULL OR node.event_id IS NOT NULL
            OR node.skill_activity_state<>header.skill_activity_state OR node.skill_activity_count IS NOT header.skill_activity_count
            OR node.tool_activity_state<>header.tool_activity_state OR node.tool_activity_count IS NOT header.tool_activity_count
            OR node.subagent_activity_state<>header.subagent_activity_state OR node.subagent_activity_count IS NOT header.subagent_activity_count
            OR node.error_activity_state<>header.error_activity_state OR node.error_activity_count IS NOT header.error_activity_count
            OR node.retry_activity_state<>header.retry_activity_state OR node.retry_activity_count IS NOT header.retry_activity_count
            OR node.token_authority<>header.token_authority OR node.token_state<>header.token_state
            OR node.available_execution_count<>header.available_execution_count OR node.total_execution_count<>1
            OR node.input_token_state<>header.input_token_state OR node.input_tokens IS NOT header.input_tokens
            OR node.output_token_state<>header.output_token_state OR node.output_tokens IS NOT header.output_tokens
            OR node.total_token_state<>header.total_token_state OR node.total_tokens IS NOT header.total_tokens
            OR node.reasoning_token_state<>header.reasoning_token_state OR node.reasoning_tokens IS NOT header.reasoning_tokens
            OR node.cache_read_token_state<>header.cache_read_token_state OR node.cache_read_tokens IS NOT header.cache_read_tokens
            OR node.cache_creation_token_state<>header.cache_creation_token_state OR node.cache_creation_tokens IS NOT header.cache_creation_tokens
            OR node.new_input_token_state<>header.new_input_token_state OR node.new_input_tokens IS NOT header.new_input_tokens
            OR node.cache_read_ratio_state<>header.cache_read_ratio_state OR node.cache_read_ratio_basis_points IS NOT header.cache_read_ratio_basis_points
            OR node.retry_relation_state<>'not_observed' OR node.recovery_relation_state<>'not_observed'
            OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)<>1
            OR NOT EXISTS(SELECT 1 FROM local_workspace_node_source_references reference
              WHERE reference.node_id=node.node_id AND reference.source_ordinal=0 AND reference.source_kind='session_run'
                AND reference.source_identity=header.source_identity AND reference.trace_id IS NULL
                AND reference.span_id IS NULL AND reference.event_id IS NULL
                AND reference.revision_input=header.source_kind||'|'||header.source_identity||'|'||header.lifecycle||'|'||header.status||'|'||COALESCE(CAST(header.start_utc_ticks AS TEXT),''))
            OR EXISTS(SELECT 1 FROM local_workspace_node_edges edge WHERE edge.node_id=node.node_id AND edge.relation_kind='parent'))
          UNION ALL
          SELECT 1 FROM local_workspace_execution_headers header WHERE header.session_id=$session_id AND NOT EXISTS(
            SELECT 1 FROM local_workspace_nodes node WHERE node.session_id=header.session_id
              AND node.source_kind='execution_root' AND node.source_identity=header.source_identity)
        ),
        ranked_events AS (
          SELECT event.*,
                 header.execution_id expected_execution_id,
                 row_number() OVER(PARTITION BY event.run_id ORDER BY event.event_id COLLATE BINARY) expected_ordinal,
                 parent.event_id valid_parent_event_id,
                 CASE WHEN event.parent_event_id IS NULL THEN local_workspace_node_id('execution_root',event.run_id)
                      WHEN parent.event_id IS NOT NULL THEN local_workspace_node_id('session_event',parent.event_id)
                      ELSE local_workspace_node_id('unknown_relation_group',event.run_id) END expected_parent_node_id,
                 CASE WHEN event.parent_event_id IS NULL OR parent.event_id IS NOT NULL THEN 'exact' ELSE 'unknown' END expected_relationship,
                 CASE event.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END expected_lifecycle,
                 CASE event.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END expected_status,
                 CASE WHEN local_workspace_ticks(event.occurred_at) IS NOT NULL THEN 'recorded'
                      WHEN event.occurred_at IS NULL THEN 'missing' ELSE 'invalid' END expected_time_authority,
                 local_workspace_ticks(event.occurred_at) expected_ticks,
                 CASE WHEN event.source_adapter='otel-exact' AND event.type='otel.span'
                            AND length(event.trace_id)=32 AND event.trace_id=lower(event.trace_id)
                            AND event.trace_id NOT GLOB '*[^0-9a-f]*'
                            AND length(event.source_event_id)=49
                            AND substr(event.source_event_id,1,33)=event.trace_id||'/' COLLATE BINARY
                            AND substr(event.source_event_id,34)=lower(substr(event.source_event_id,34))
                            AND substr(event.source_event_id,34) NOT GLOB '*[^0-9a-f]*'
                      THEN event.trace_id END expected_trace,
                 CASE WHEN event.source_adapter='otel-exact' AND event.type='otel.span'
                            AND length(event.trace_id)=32 AND event.trace_id=lower(event.trace_id)
                            AND event.trace_id NOT GLOB '*[^0-9a-f]*'
                            AND length(event.source_event_id)=49
                            AND substr(event.source_event_id,1,33)=event.trace_id||'/' COLLATE BINARY
                            AND substr(event.source_event_id,34)=lower(substr(event.source_event_id,34))
                            AND substr(event.source_event_id,34) NOT GLOB '*[^0-9a-f]*'
                      THEN substr(event.source_event_id,34) END expected_span
          FROM session_events event
          LEFT JOIN local_workspace_execution_headers header
            ON header.session_id=event.session_id AND header.source_kind='session_run'
           AND header.source_identity=event.run_id COLLATE BINARY
          LEFT JOIN session_events parent
            ON parent.event_id=event.parent_event_id AND parent.session_id=event.session_id AND parent.run_id=event.run_id
          WHERE event.session_id=$session_id AND event.run_id IS NOT NULL
        ),
        invalid_events AS (
          SELECT 1 FROM local_workspace_nodes node
          LEFT JOIN ranked_events owner ON owner.event_id=node.source_identity COLLATE BINARY
          WHERE node.session_id=$session_id AND node.source_kind='session_event' AND (
            owner.event_id IS NULL OR owner.expected_execution_id IS NULL
            OR node.node_id<>local_workspace_node_id('session_event',owner.event_id)
            OR node.execution_id<>owner.expected_execution_id OR node.source_identity<>owner.event_id
            OR node.source_ordinal<>owner.expected_ordinal OR node.parent_node_id IS NOT owner.expected_parent_node_id
            OR node.relationship_authority<>owner.expected_relationship
            OR node.kind<>local_workspace_node_kind(owner.type) OR node.name_state<>'recorded' OR node.name_text IS NOT owner.type
            OR node.lifecycle<>owner.expected_lifecycle OR node.status<>owner.expected_status
            OR node.time_authority<>owner.expected_time_authority OR node.start_utc_ticks IS NOT owner.expected_ticks
            OR node.end_utc_ticks IS NOT CASE WHEN owner.expected_status='active' THEN NULL ELSE owner.expected_ticks END
            OR node.duration_ms IS NOT CASE WHEN owner.expected_status='active' OR owner.expected_ticks IS NULL THEN NULL ELSE 0 END
            OR node.trace_id IS NOT owner.expected_trace OR node.span_id IS NOT owner.expected_span OR node.event_id IS NOT owner.event_id
            OR node.skill_activity_state<>'not_observed' OR node.skill_activity_count IS NOT NULL
            OR node.tool_activity_state<>CASE WHEN local_workspace_node_kind(owner.type)='tool' THEN 'recorded' ELSE 'not_observed' END
            OR node.tool_activity_count IS NOT CASE WHEN local_workspace_node_kind(owner.type)='tool' THEN 1 END
            OR node.subagent_activity_state<>CASE WHEN local_workspace_node_kind(owner.type)='subagent' THEN 'recorded' ELSE 'not_observed' END
            OR node.subagent_activity_count IS NOT CASE WHEN local_workspace_node_kind(owner.type)='subagent' THEN 1 END
            OR node.error_activity_state<>CASE WHEN local_workspace_node_kind(owner.type)='error' THEN 'recorded' ELSE 'not_observed' END
            OR node.error_activity_count IS NOT CASE WHEN local_workspace_node_kind(owner.type)='error' THEN 1 END
            OR node.retry_activity_state<>CASE WHEN local_workspace_node_kind(owner.type)='retry' THEN 'recorded' ELSE 'not_observed' END
            OR node.retry_activity_count IS NOT CASE WHEN local_workspace_node_kind(owner.type)='retry' THEN 1 END
            OR node.token_authority<>'none' OR node.token_state<>'not_observed'
            OR node.available_execution_count<>0 OR node.total_execution_count<>1
            OR node.input_token_state<>'not_observed' OR node.input_tokens IS NOT NULL
            OR node.output_token_state<>'not_observed' OR node.output_tokens IS NOT NULL
            OR node.total_token_state<>'not_observed' OR node.total_tokens IS NOT NULL
            OR node.reasoning_token_state<>'not_observed' OR node.reasoning_tokens IS NOT NULL
            OR node.cache_read_token_state<>'not_observed' OR node.cache_read_tokens IS NOT NULL
            OR node.cache_creation_token_state<>'not_observed' OR node.cache_creation_tokens IS NOT NULL
            OR node.new_input_token_state<>'not_observed' OR node.new_input_tokens IS NOT NULL
            OR node.cache_read_ratio_state<>'not_observed' OR node.cache_read_ratio_basis_points IS NOT NULL
            OR node.retry_relation_state<>'not_observed' OR node.recovery_relation_state<>'not_observed'
            OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)<>1
            OR NOT EXISTS(SELECT 1 FROM local_workspace_node_source_references reference
              WHERE reference.node_id=node.node_id AND reference.source_ordinal=0 AND reference.source_kind='session_event'
                AND reference.source_identity=owner.event_id AND reference.trace_id IS owner.expected_trace
                AND reference.span_id IS owner.expected_span AND reference.event_id=owner.event_id
                AND reference.revision_input=owner.source_adapter||'|'||owner.source_event_id||'|'||owner.type||'|'||COALESCE(owner.occurred_at,''))
            OR (SELECT COUNT(*) FROM local_workspace_node_edges edge
                WHERE edge.node_id=node.node_id AND edge.relation_kind='parent')<>
               CASE WHEN owner.expected_relationship='exact' THEN 1 ELSE 0 END
            OR (owner.expected_relationship='exact' AND NOT EXISTS(
              SELECT 1 FROM local_workspace_node_edges edge WHERE edge.node_id=node.node_id
                AND edge.related_node_id=owner.expected_parent_node_id AND edge.relation_kind='parent'
                AND edge.relationship_authority='exact' AND edge.source_ordinal=node.source_ordinal))
            OR (owner.expected_relationship='unknown' AND EXISTS(
              SELECT 1 FROM local_workspace_node_edges edge WHERE edge.node_id=node.node_id AND edge.relation_kind='parent'))
            OR (owner.expected_relationship='unknown' AND NOT EXISTS(
              SELECT 1 FROM local_workspace_nodes unknown_group WHERE unknown_group.session_id=node.session_id
                AND unknown_group.node_id=owner.expected_parent_node_id AND unknown_group.source_kind='unknown_relation_group'
                AND unknown_group.execution_id=owner.expected_execution_id)))
          UNION ALL
          SELECT 1 FROM ranked_events owner WHERE NOT EXISTS(
            SELECT 1 FROM local_workspace_nodes node WHERE node.session_id=owner.session_id
              AND node.source_kind='session_event' AND node.source_identity=owner.event_id)
        ),
        expected_unknown_group_candidates AS (
          SELECT owner.session_id,owner.run_id source_identity,owner.expected_execution_id execution_id,
                 (SELECT COUNT(*)+1 FROM session_events event
                  WHERE event.session_id=owner.session_id AND event.run_id=owner.run_id) expected_ordinal,
                 1 priority
          FROM ranked_events owner
          WHERE owner.expected_relationship='unknown' AND owner.expected_execution_id IS NOT NULL
          GROUP BY owner.session_id,owner.run_id,owner.expected_execution_id
          UNION ALL
          SELECT child.session_id,header.source_identity,child.execution_id,
                 (SELECT COUNT(*)+2 FROM session_events event
                  WHERE event.session_id=child.session_id AND event.run_id=header.source_identity),
                 2
          FROM local_workspace_nodes child
          JOIN local_workspace_execution_headers header ON header.execution_id=child.execution_id AND header.session_id=child.session_id
          WHERE child.session_id=$session_id AND child.source_kind='skill_invocation'
            AND child.relationship_authority='unknown'
            AND child.parent_node_id=local_workspace_node_id('unknown_relation_group',header.source_identity)
            AND NOT EXISTS(SELECT 1 FROM session_events event
              LEFT JOIN session_events parent ON parent.event_id=event.parent_event_id AND parent.run_id=event.run_id
              WHERE event.session_id=child.session_id AND event.run_id=header.source_identity
                AND event.parent_event_id IS NOT NULL AND parent.event_id IS NULL)
          UNION ALL
          SELECT child.session_id,header.source_identity,child.execution_id,
                 (SELECT COUNT(*)+2 FROM session_events event
                  WHERE event.session_id=child.session_id AND event.run_id=header.source_identity)+
                 (SELECT COUNT(*) FROM local_workspace_nodes skill
                  WHERE skill.session_id=child.session_id AND skill.execution_id=child.execution_id
                    AND skill.source_kind='skill_invocation'),
                 3
          FROM local_workspace_nodes child
          JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=child.node_id
          JOIN local_workspace_execution_headers header
            ON header.execution_id=child.execution_id AND header.session_id=child.session_id
          WHERE child.session_id=$session_id AND child.source_kind='semantic_tool'
            AND child.relationship_authority='unknown'
            AND child.parent_node_id=local_workspace_node_id('unknown_relation_group',header.source_identity)
            AND receipt.semantic_kind='tool' AND receipt.source_family='otel'
        ),
        expected_unknown_groups AS (
          SELECT DISTINCT candidate.session_id,candidate.source_identity,candidate.execution_id,candidate.expected_ordinal
          FROM expected_unknown_group_candidates candidate
          WHERE candidate.priority=(SELECT MIN(other.priority) FROM expected_unknown_group_candidates other
            WHERE other.session_id=candidate.session_id AND other.source_identity=candidate.source_identity COLLATE BINARY)
        ),
        invalid_unknown_groups AS (
          SELECT 1 FROM local_workspace_nodes node
          LEFT JOIN expected_unknown_groups expected
            ON expected.session_id=node.session_id AND expected.source_identity=node.source_identity COLLATE BINARY
          WHERE node.session_id=$session_id AND node.source_kind='unknown_relation_group' AND (
            expected.source_identity IS NULL
            OR node.node_id<>local_workspace_node_id('unknown_relation_group',expected.source_identity)
            OR node.execution_id<>expected.execution_id OR node.source_ordinal<>expected.expected_ordinal
            OR node.parent_node_id IS NOT NULL OR node.relationship_authority<>'unknown'
            OR node.kind<>'unknown_relation_group' OR node.name_state<>'not_observed' OR node.name_text IS NOT NULL
            OR node.lifecycle<>'unknown' OR node.status<>'unknown' OR node.time_authority<>'missing'
            OR node.start_utc_ticks IS NOT NULL OR node.end_utc_ticks IS NOT NULL OR node.duration_ms IS NOT NULL
            OR node.skill_activity_state<>'not_observed' OR node.skill_activity_count IS NOT NULL
            OR node.tool_activity_state<>'not_observed' OR node.tool_activity_count IS NOT NULL
            OR node.subagent_activity_state<>'not_observed' OR node.subagent_activity_count IS NOT NULL
            OR node.error_activity_state<>'not_observed' OR node.error_activity_count IS NOT NULL
            OR node.retry_activity_state<>'not_observed' OR node.retry_activity_count IS NOT NULL
            OR node.token_authority<>'none' OR node.token_state<>'not_observed'
            OR node.available_execution_count<>0 OR node.total_execution_count<>1
            OR node.input_token_state<>'not_observed' OR node.input_tokens IS NOT NULL
            OR node.output_token_state<>'not_observed' OR node.output_tokens IS NOT NULL
            OR node.total_token_state<>'not_observed' OR node.total_tokens IS NOT NULL
            OR node.reasoning_token_state<>'not_observed' OR node.reasoning_tokens IS NOT NULL
            OR node.cache_read_token_state<>'not_observed' OR node.cache_read_tokens IS NOT NULL
            OR node.cache_creation_token_state<>'not_observed' OR node.cache_creation_tokens IS NOT NULL
            OR node.new_input_token_state<>'not_observed' OR node.new_input_tokens IS NOT NULL
            OR node.cache_read_ratio_state<>'not_observed' OR node.cache_read_ratio_basis_points IS NOT NULL
            OR node.trace_id IS NOT NULL OR node.span_id IS NOT NULL OR node.event_id IS NOT NULL
            OR node.otel_source_identity IS NOT NULL OR node.sdk_source_identity IS NOT NULL
            OR EXISTS(SELECT 1 FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)
            OR EXISTS(SELECT 1 FROM local_workspace_node_edges edge WHERE edge.node_id=node.node_id))
          UNION ALL
          SELECT 1 FROM expected_unknown_groups expected WHERE NOT EXISTS(
            SELECT 1 FROM local_workspace_nodes node WHERE node.session_id=expected.session_id
              AND node.source_kind='unknown_relation_group' AND node.source_identity=expected.source_identity COLLATE BINARY)
        )
        SELECT EXISTS(
          SELECT 1 FROM invalid_headers
          UNION ALL SELECT 1 FROM invalid_roots
          UNION ALL SELECT 1 FROM invalid_events
          UNION ALL SELECT 1 FROM invalid_unknown_groups);
        """;

    private static async Task<bool> SemanticGraphInvalid(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string sql,
        CancellationToken token)
    {
        using var command = Command(connection, transaction, sql, sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private const string SemanticReceiptCoverageSql = """
        SELECT EXISTS(
          SELECT 1
          FROM local_workspace_nodes node
          LEFT JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
          WHERE node.session_id=$session_id AND node.source_kind IN ('semantic_tool','semantic_subagent') AND (
            receipt.node_id IS NULL OR receipt.carrier_digest<>node.source_identity
            OR receipt.semantic_kind<>CASE node.source_kind WHEN 'semantic_tool' THEN 'tool' ELSE 'subagent' END
            OR receipt.source_family NOT IN ('otel','session_sdk','claude_hook')
            OR node.source_kind='semantic_subagent' AND receipt.source_family NOT IN ('session_sdk','claude_hook')
            OR node.kind<>CASE node.source_kind WHEN 'semantic_tool' THEN 'tool' ELSE 'subagent' END
            OR node.skill_activity_state<>'not_observed' OR node.skill_activity_count IS NOT NULL
            OR node.tool_activity_state<>CASE node.source_kind WHEN 'semantic_tool' THEN 'recorded' ELSE 'not_observed' END
            OR node.tool_activity_count IS NOT CASE node.source_kind WHEN 'semantic_tool' THEN 1 END
            OR node.subagent_activity_state<>CASE node.source_kind WHEN 'semantic_subagent' THEN 'recorded' ELSE 'not_observed' END
            OR node.subagent_activity_count IS NOT CASE node.source_kind WHEN 'semantic_subagent' THEN 1 END
            OR node.error_activity_state<>'not_observed' OR node.error_activity_count IS NOT NULL
            OR node.retry_activity_state<>'not_observed' OR node.retry_activity_count IS NOT NULL
            OR node.token_authority<>'none' OR node.token_state<>'not_observed'
            OR node.available_execution_count<>0 OR node.total_execution_count<>1
            OR node.input_token_state<>'not_observed' OR node.input_tokens IS NOT NULL
            OR node.output_token_state<>'not_observed' OR node.output_tokens IS NOT NULL
            OR node.total_token_state<>'not_observed' OR node.total_tokens IS NOT NULL
            OR node.reasoning_token_state<>'not_observed' OR node.reasoning_tokens IS NOT NULL
            OR node.cache_read_token_state<>'not_observed' OR node.cache_read_tokens IS NOT NULL
            OR node.cache_creation_token_state<>'not_observed' OR node.cache_creation_tokens IS NOT NULL
            OR node.new_input_token_state<>'not_observed' OR node.new_input_tokens IS NOT NULL
            OR node.cache_read_ratio_state<>'not_observed' OR node.cache_read_ratio_basis_points IS NOT NULL
            OR node.retry_relation_state<>'not_observed' OR node.recovery_relation_state<>'not_observed'
            OR node.parent_node_id IS NULL OR node.relationship_authority NOT IN ('exact','unknown')
            OR node.relationship_authority='unknown' AND receipt.semantic_kind<>'tool'
            OR (SELECT COUNT(*) FROM local_workspace_node_edges edge
                WHERE edge.node_id=node.node_id AND edge.relation_kind='parent')<>
               CASE node.relationship_authority WHEN 'exact' THEN 1 ELSE 0 END
            OR node.relationship_authority='exact' AND NOT EXISTS(SELECT 1 FROM local_workspace_node_edges edge
                WHERE edge.node_id=node.node_id AND edge.related_node_id=node.parent_node_id
                  AND edge.relation_kind='parent' AND edge.relationship_authority='exact'
                  AND edge.source_ordinal=node.source_ordinal)
            OR node.relationship_authority='unknown' AND NOT EXISTS(
                SELECT 1 FROM local_workspace_nodes relation_group
                JOIN local_workspace_execution_headers execution
                  ON execution.execution_id=node.execution_id AND execution.session_id=node.session_id
                WHERE relation_group.node_id=node.parent_node_id
                  AND relation_group.session_id=node.session_id
                  AND relation_group.execution_id=node.execution_id
                  AND relation_group.source_kind='unknown_relation_group'
                  AND relation_group.source_identity=execution.source_identity COLLATE BINARY)
            OR node.source_kind='semantic_tool' AND NOT EXISTS(
                SELECT 1 FROM local_workspace_tool_metadata metadata WHERE metadata.node_id=node.node_id)
            OR node.source_kind='semantic_subagent' AND NOT EXISTS(
                SELECT 1 FROM local_workspace_subagent_lifecycle lifecycle WHERE lifecycle.node_id=node.node_id))
          UNION ALL
          SELECT 1
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          WHERE node.session_id=$session_id AND node.source_kind NOT IN ('semantic_tool','semantic_subagent'));
        """;

    private const string OtelToolOwnerValidationSql = """
        WITH base AS (
          SELECT e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,e.occurred_at,
                 m.raw_record_id,m.span_ordinal,m.trace_id,m.span_id,m.parent_span_id,m.status,m.start_time,m.end_time,
                 local_workspace_ticks(m.start_time) start_ticks,local_workspace_ticks(m.end_time) end_ticks,
                 m.tool_name,m.mcp_tool_name,m.mcp_server_hash,
                 local_workspace_semantic_digest('otel_tool',m.trace_id,m.span_id) carrier_digest,
                 (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=m.trace_id COLLATE BINARY AND lower(owner.span_id)=m.span_id COLLATE BINARY) monitor_owner_count,
                 (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                    AND owner.type='otel.span' COLLATE BINARY
                    AND lower(owner.trace_id)=m.trace_id COLLATE BINARY
                    AND lower(owner.source_event_id)=m.trace_id||'/'||m.span_id COLLATE BINARY) event_owner_count
          FROM session_events e JOIN monitor_spans m
            ON e.source_adapter='otel-exact' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY
           AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
          JOIN session_runs run ON run.session_id=e.session_id AND run.run_id=e.run_id
          WHERE e.session_id=$session_id AND e.run_id IS NOT NULL AND e.type='otel.span' COLLATE BINARY
            AND m.operation='execute_tool' COLLATE BINARY AND m.category IN ('tool_call','error')
        ),
        lifecycle AS (
          SELECT base.*,CASE WHEN start_ticks IS NOT NULL THEN 'otel.tool.started' ELSE 'otel.tool.observed' END lifecycle_type
          FROM base
          UNION ALL
          SELECT base.*,CASE WHEN status='error' COLLATE BINARY THEN 'otel.tool.failed' ELSE 'otel.tool.completed' END
          FROM base WHERE status IN ('ok','error') OR end_ticks IS NOT NULL
        ),
        groups AS (
          SELECT carrier_digest,MIN(session_id) session_id,MIN(run_id) run_id,MIN(event_id) event_id,
                 MIN(source_adapter) source_adapter,MIN(source_event_id) source_event_id,MIN(occurred_at) occurred_at,
                 MIN(trace_id) trace_id,MIN(span_id) span_id,MIN(parent_span_id) parent_span_id,
                 MIN(status) status,MIN(start_time) start_time,MIN(end_time) end_time,
                 MIN(start_ticks) start_ticks,MIN(end_ticks) end_ticks,
                 MIN(tool_name) tool_name,COUNT(DISTINCT tool_name) tool_name_count,
                 MIN(mcp_tool_name) mcp_tool_name,COUNT(DISTINCT mcp_tool_name) mcp_tool_name_count,
                 MIN(mcp_server_hash) mcp_server_hash,COUNT(DISTINCT mcp_server_hash) mcp_server_count,
                 COUNT(DISTINCT session_id) session_count,COUNT(DISTINCT run_id) run_count,
                 COUNT(DISTINCT event_id) event_count,
                 COUNT(DISTINCT CAST(raw_record_id AS TEXT)||':'||CAST(span_ordinal AS TEXT)) span_owner_count,
                 MAX(monitor_owner_count) monitor_owner_count,MAX(event_owner_count) event_owner_count,
                 MIN(lifecycle_type) min_type,MAX(lifecycle_type) max_type,COUNT(DISTINCT lifecycle_type) type_count
          FROM lifecycle GROUP BY carrier_digest
        ),
        expected AS (
          SELECT groups.*,
                 CASE WHEN parent_span_id IS NULL THEN 0 ELSE (
                   SELECT COUNT(*) FROM session_events parent
                   WHERE parent.session_id=groups.session_id AND parent.run_id=groups.run_id
                     AND parent.source_adapter='otel-exact' COLLATE BINARY AND parent.type='otel.span' COLLATE BINARY
                     AND parent.trace_id=groups.trace_id COLLATE BINARY
                     AND parent.source_event_id=groups.trace_id||'/'||groups.parent_span_id COLLATE BINARY) END parent_count,
                 CASE WHEN parent_span_id IS NULL THEN 0 ELSE (
                   SELECT COUNT(*) FROM monitor_spans parent_owner
                   WHERE lower(parent_owner.trace_id)=groups.trace_id COLLATE BINARY
                     AND lower(parent_owner.span_id)=groups.parent_span_id COLLATE BINARY) END parent_monitor_owner_count,
                 CASE WHEN parent_span_id IS NULL THEN NULL ELSE (
                   SELECT MIN(parent.event_id) FROM session_events parent
                   WHERE parent.session_id=groups.session_id AND parent.run_id=groups.run_id
                     AND parent.source_adapter='otel-exact' COLLATE BINARY AND parent.type='otel.span' COLLATE BINARY
                     AND parent.trace_id=groups.trace_id COLLATE BINARY
                     AND parent.source_event_id=groups.trace_id||'/'||groups.parent_span_id COLLATE BINARY) END parent_event_id,
                 source_adapter||'|'||source_event_id||'|'||min_type||'|'||max_type||'|'||CAST(type_count AS TEXT)||'|'||
                   COALESCE(occurred_at,'')||'|otel-exact|normalized-tool-span|v1' reference_revision
          FROM groups
        ),
        invalid_persisted AS (
          SELECT 1
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          LEFT JOIN expected owner ON owner.carrier_digest=receipt.carrier_digest
          LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
          LEFT JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
          WHERE node.session_id=$session_id AND receipt.semantic_kind='tool' AND receipt.source_family='otel'
            AND (
              owner.carrier_digest IS NULL OR owner.session_count<>1 OR owner.run_count<>1 OR owner.event_count<>1
              OR owner.span_owner_count<>1 OR owner.monitor_owner_count<>1 OR owner.event_owner_count<>1
              OR length(owner.trace_id)<>32 OR owner.trace_id<>lower(owner.trace_id) OR owner.trace_id GLOB '*[^0-9a-f]*'
              OR length(owner.span_id)<>16 OR owner.span_id<>lower(owner.span_id) OR owner.span_id GLOB '*[^0-9a-f]*'
              OR receipt.scope_kind<>'otel_span' OR receipt.authority_receipt<>'otel-exact|normalized-tool-span|v1'
              OR node.source_kind<>'semantic_tool' OR node.kind<>'tool' OR node.source_identity<>owner.carrier_digest
              OR node.node_id<>local_workspace_node_id('semantic_tool',owner.carrier_digest)
              OR node.session_id<>owner.session_id OR node.execution_id<>local_workspace_execution_id('session_run',owner.run_id)
              OR execution.session_id<>owner.session_id OR execution.source_kind<>'session_run' OR execution.source_identity<>owner.run_id
              OR node.trace_id IS NOT owner.trace_id OR node.span_id IS NOT owner.span_id OR node.event_id IS NOT owner.event_id
              OR node.parent_node_id<>CASE WHEN owner.parent_span_id IS NULL THEN local_workspace_node_id('execution_root',owner.run_id)
                                          WHEN owner.parent_count=1 AND owner.parent_monitor_owner_count=1 THEN local_workspace_node_id('session_event',owner.parent_event_id)
                                          ELSE local_workspace_node_id('unknown_relation_group',owner.run_id) END
              OR node.relationship_authority<>CASE WHEN owner.parent_span_id IS NULL
                                                        OR owner.parent_count=1 AND owner.parent_monitor_owner_count=1 THEN 'exact' ELSE 'unknown' END
              OR node.name_state<>CASE WHEN owner.tool_name_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR node.name_text IS NOT CASE WHEN owner.tool_name_count=1 THEN owner.tool_name END
              OR node.lifecycle<>CASE WHEN owner.status='error' COLLATE BINARY THEN 'failed'
                                     WHEN owner.status='ok' COLLATE BINARY OR owner.end_ticks IS NOT NULL THEN 'completed'
                                     WHEN owner.start_ticks IS NOT NULL THEN 'started' ELSE 'unknown' END
              OR node.status<>CASE owner.status WHEN 'error' THEN 'failed' WHEN 'ok' THEN 'completed' ELSE 'unknown' END
              OR node.time_authority<>CASE WHEN owner.status COLLATE BINARY IN ('ok','error') OR owner.end_ticks IS NOT NULL THEN
                                             CASE WHEN owner.start_ticks IS NOT NULL AND owner.end_ticks>=owner.start_ticks THEN 'recorded' ELSE 'invalid' END
                                           WHEN owner.start_time IS NULL AND owner.end_time IS NULL THEN 'missing'
                                           WHEN owner.start_ticks IS NULL OR owner.end_time IS NOT NULL AND (owner.end_ticks IS NULL OR owner.end_ticks<owner.start_ticks) THEN 'invalid'
                                           ELSE 'recorded' END
              OR node.start_utc_ticks IS NOT CASE WHEN (owner.status COLLATE BINARY IN ('ok','error') OR owner.end_ticks IS NOT NULL)
                                                          AND (owner.start_ticks IS NULL OR owner.end_ticks IS NULL OR owner.end_ticks<owner.start_ticks) THEN NULL
                                                   WHEN owner.start_ticks IS NOT NULL AND (owner.end_time IS NULL OR owner.end_ticks>=owner.start_ticks) THEN owner.start_ticks END
              OR node.end_utc_ticks IS NOT CASE WHEN owner.start_ticks IS NOT NULL AND owner.end_ticks>=owner.start_ticks THEN owner.end_ticks END
              OR node.duration_ms IS NOT CASE WHEN owner.start_ticks IS NOT NULL AND owner.end_ticks>=owner.start_ticks THEN (owner.end_ticks-owner.start_ticks)/10000 END
              OR metadata.node_id IS NULL
              OR metadata.caller_state<>CASE WHEN owner.parent_count=1 AND owner.parent_monitor_owner_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR metadata.caller_node_id IS NOT CASE WHEN owner.parent_count=1 AND owner.parent_monitor_owner_count=1
                                                      THEN local_workspace_node_id('session_event',owner.parent_event_id) END
              OR metadata.started_state<>CASE WHEN owner.start_ticks IS NOT NULL THEN 'recorded' ELSE 'not_observed' END
              OR metadata.completed_state<>CASE WHEN owner.status='error' COLLATE BINARY THEN 'not_observed'
                                                WHEN owner.status='ok' COLLATE BINARY OR owner.end_ticks IS NOT NULL THEN 'recorded' ELSE 'not_observed' END
              OR metadata.failed_state<>CASE WHEN owner.status='error' COLLATE BINARY THEN 'recorded' ELSE 'not_observed' END
              OR metadata.exit_state<>'source_unsupported' OR metadata.exit_code IS NOT NULL
              OR metadata.mcp_server_identity_state<>CASE WHEN owner.mcp_server_count=1 AND length(owner.mcp_server_hash)=64
                    AND owner.mcp_server_hash=lower(owner.mcp_server_hash) AND owner.mcp_server_hash NOT GLOB '*[^0-9a-f]*' THEN 'recorded' ELSE 'not_observed' END
              OR metadata.mcp_server_identity IS NOT CASE WHEN owner.mcp_server_count=1 AND length(owner.mcp_server_hash)=64
                    AND owner.mcp_server_hash=lower(owner.mcp_server_hash) AND owner.mcp_server_hash NOT GLOB '*[^0-9a-f]*' THEN owner.mcp_server_hash END
              OR metadata.mcp_server_name_state<>'source_unsupported' OR metadata.mcp_server_name IS NOT NULL
              OR metadata.mcp_tool_name_state<>CASE WHEN owner.mcp_tool_name_count=1 THEN 'recorded'
                                                     WHEN owner.mcp_tool_name_count=0 THEN 'not_observed' ELSE 'invalid' END
              OR metadata.mcp_tool_name IS NOT CASE WHEN owner.mcp_tool_name_count=1 THEN owner.mcp_tool_name END
              OR metadata.retry_state<>'not_observed' OR metadata.recovery_state<>'not_observed'
              OR metadata.child_activity_state<>'not_observed' OR metadata.child_activity_count IS NOT NULL
              OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)<>1
              OR NOT EXISTS(SELECT 1 FROM local_workspace_node_source_references reference
                    WHERE reference.node_id=node.node_id AND reference.source_ordinal=0 AND reference.source_kind='otel_span'
                      AND reference.source_identity=owner.event_id AND reference.trace_id=owner.trace_id AND reference.span_id=owner.span_id
                      AND reference.event_id=owner.event_id AND reference.revision_input=owner.reference_revision)
              OR (SELECT COUNT(*) FROM local_workspace_node_edges edge
                  WHERE edge.node_id=node.node_id AND edge.relation_kind='parent')<>
                 CASE WHEN owner.parent_span_id IS NULL OR owner.parent_count=1 AND owner.parent_monitor_owner_count=1 THEN 1 ELSE 0 END
              OR (owner.parent_span_id IS NULL OR owner.parent_count=1 AND owner.parent_monitor_owner_count=1) AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_node_edges edge
                  WHERE edge.node_id=node.node_id AND edge.related_node_id=node.parent_node_id
                    AND edge.relation_kind='parent' AND edge.relationship_authority='exact'
                    AND edge.source_ordinal=node.source_ordinal)
              OR owner.parent_span_id IS NOT NULL
                 AND (owner.parent_count<>1 OR owner.parent_monitor_owner_count<>1) AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_nodes relation_group
                  WHERE relation_group.node_id=node.parent_node_id
                    AND relation_group.session_id=node.session_id
                    AND relation_group.execution_id=node.execution_id
                    AND relation_group.source_kind='unknown_relation_group'
                    AND relation_group.source_identity=owner.run_id COLLATE BINARY)
            )
        ),
        missing_persisted AS (
          SELECT 1 FROM expected owner
          WHERE owner.session_id=$session_id AND owner.session_count=1 AND owner.run_count=1 AND owner.event_count=1
            AND owner.span_owner_count=1 AND owner.monitor_owner_count=1 AND owner.event_owner_count=1
            AND length(owner.trace_id)=32 AND owner.trace_id=lower(owner.trace_id) AND owner.trace_id NOT GLOB '*[^0-9a-f]*'
            AND length(owner.span_id)=16 AND owner.span_id=lower(owner.span_id) AND owner.span_id NOT GLOB '*[^0-9a-f]*'
            AND NOT EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE node.session_id=$session_id AND receipt.semantic_kind='tool' AND receipt.source_family='otel'
                AND receipt.carrier_digest=owner.carrier_digest)
        )
        SELECT EXISTS(SELECT 1 FROM invalid_persisted UNION ALL SELECT 1 FROM missing_persisted);
        """;

    private const string SdkToolOwnerValidationSql = """
        WITH starts AS (
          SELECT local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                   local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,event.source_event_id)) carrier_digest,
                 event.session_id,event.run_id,event.event_id,event.parent_event_id,event.source_adapter,event.source_event_id,event.type,event.occurred_at,
                 event.trace_id,event.source_adapter||'|exact_sdk_tool|v1' authority_receipt
          FROM session_events event JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
          JOIN session_native_ids native ON native.session_id=event.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
          WHERE event.session_id=$session_id AND event.source_surface='copilot-sdk' COLLATE BINARY
            AND run.source_surface='copilot-sdk' COLLATE BINARY AND event.source_adapter='copilot-sdk-stream' COLLATE BINARY
            AND event.type='tool.execution_start' AND event.source_event_id IS NOT NULL AND length(event.source_event_id)>0
            AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
            AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=event.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
            AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=event.session_id
              AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
            AND EXISTS(SELECT 1 FROM session_events completion
              WHERE completion.session_id=event.session_id AND completion.run_id=event.run_id
                AND completion.source_surface=event.source_surface COLLATE BINARY
                AND completion.source_adapter=event.source_adapter COLLATE BINARY
                AND completion.type='tool.execution_complete' AND completion.parent_event_id=event.event_id)
        ),
        completions AS (
          SELECT local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                   local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,parent.source_event_id)) carrier_digest,
                 event.session_id,event.run_id,event.event_id,event.parent_event_id,event.source_adapter,event.source_event_id,event.type,event.occurred_at,
                 event.trace_id,event.source_adapter||'|exact_sdk_tool|v1' authority_receipt
          FROM session_events event JOIN session_events parent
            ON parent.event_id=event.parent_event_id AND parent.session_id=event.session_id AND parent.run_id=event.run_id
           AND parent.source_adapter=event.source_adapter COLLATE BINARY AND parent.type='tool.execution_start'
          JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
          JOIN session_native_ids native ON native.session_id=event.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
          WHERE event.session_id=$session_id AND event.source_surface='copilot-sdk' COLLATE BINARY
            AND parent.source_surface='copilot-sdk' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
            AND event.source_adapter='copilot-sdk-stream' COLLATE BINARY AND event.type='tool.execution_complete'
            AND parent.source_event_id IS NOT NULL AND length(parent.source_event_id)>0
            AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
            AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=event.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
            AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=event.session_id
              AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
        ),
        candidates AS (SELECT * FROM starts UNION ALL SELECT * FROM completions),
        groups AS (
          SELECT carrier_digest,MIN(session_id) session_id,MIN(run_id) run_id,
                 COUNT(DISTINCT session_id) session_count,COUNT(DISTINCT run_id) run_count,
                 COUNT(DISTINCT authority_receipt) authority_count,MIN(authority_receipt) authority_receipt,
                 COUNT(DISTINCT event_id) reference_count,
                 SUM(type='tool.execution_start') started_count,SUM(type='tool.execution_complete') completed_count,
                 COUNT(DISTINCT trace_id) technical_identity_count,
                 MIN(CASE WHEN type='tool.execution_start' THEN event_id END) start_event_id,
                 MIN(CASE WHEN type='tool.execution_start' THEN parent_event_id END) start_parent_event_id,
                 MIN(CASE WHEN type='tool.execution_start' THEN source_adapter END) start_adapter
          FROM candidates GROUP BY carrier_digest
        ),
        expected AS (
          SELECT groups.*,
                 CASE WHEN started_count=1 AND start_parent_event_id IS NOT NULL THEN (
                   SELECT COUNT(*) FROM session_events parent
                   WHERE parent.event_id=groups.start_parent_event_id AND parent.session_id=groups.session_id
                     AND parent.run_id=groups.run_id AND parent.source_adapter=groups.start_adapter COLLATE BINARY) ELSE 0 END parent_count
          FROM groups
        ),
        expected_references AS (
          SELECT candidate.carrier_digest,candidate.event_id,candidate.source_adapter,candidate.source_event_id,
                 candidate.source_adapter||'|'||candidate.source_event_id||'|'||candidate.type||'|'||candidate.type||'|1|'||
                   COALESCE(candidate.occurred_at,'')||'|'||candidate.authority_receipt revision_input,
                  row_number() OVER(PARTITION BY candidate.carrier_digest
                    ORDER BY (candidate.type='tool.execution_start') DESC,candidate.event_id COLLATE BINARY)-1 source_ordinal
          FROM candidates candidate
        ),
        invalid_persisted AS (
          SELECT 1
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          LEFT JOIN expected owner ON owner.carrier_digest=receipt.carrier_digest
          LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
          LEFT JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
          WHERE node.session_id=$session_id AND receipt.semantic_kind='tool' AND receipt.source_family='session_sdk'
            AND (
              owner.carrier_digest IS NULL OR owner.session_count<>1 OR owner.run_count<>1 OR owner.authority_count<>1
              OR owner.reference_count>4096 OR owner.technical_identity_count<>0
              OR receipt.scope_kind<>'native_run' OR receipt.authority_receipt<>owner.authority_receipt
              OR node.source_kind<>'semantic_tool' OR node.kind<>'tool' OR node.source_identity<>owner.carrier_digest
              OR node.node_id<>local_workspace_node_id('semantic_tool',owner.carrier_digest)
              OR node.session_id<>owner.session_id OR node.execution_id<>local_workspace_execution_id('session_run',owner.run_id)
              OR execution.session_id<>owner.session_id OR execution.source_kind<>'session_run' OR execution.source_identity<>owner.run_id
              OR node.trace_id IS NOT NULL OR node.span_id IS NOT NULL OR node.event_id IS NOT NULL
               OR node.parent_node_id<>CASE WHEN owner.start_parent_event_id IS NULL THEN local_workspace_node_id('execution_root',owner.run_id)
                                           WHEN owner.parent_count=1 THEN local_workspace_node_id('session_event',owner.start_parent_event_id)
                                           ELSE local_workspace_node_id('unknown_relation_group',owner.run_id) END
               OR node.relationship_authority<>CASE WHEN owner.start_parent_event_id IS NULL OR owner.parent_count=1 THEN 'exact' ELSE 'unknown' END
               OR node.name_state<>'not_observed' OR node.name_text IS NOT NULL
              OR node.lifecycle<>CASE WHEN owner.authority_count=1 AND owner.completed_count=1 AND owner.started_count<=1 THEN 'completed'
                                     WHEN owner.authority_count=1 AND owner.started_count=1 AND owner.completed_count=0 THEN 'started' ELSE 'unknown' END
              OR node.status<>'unknown'
              OR node.time_authority<>'missing' OR node.start_utc_ticks IS NOT NULL OR node.end_utc_ticks IS NOT NULL OR node.duration_ms IS NOT NULL
              OR metadata.node_id IS NULL
              OR metadata.caller_state<>CASE WHEN owner.parent_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR metadata.caller_node_id IS NOT CASE WHEN owner.parent_count=1 THEN local_workspace_node_id('session_event',owner.start_parent_event_id) END
              OR metadata.started_state<>CASE WHEN owner.started_count>1 OR owner.reference_count>16 OR owner.authority_count>1 THEN 'inconsistent'
                                               WHEN owner.started_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR metadata.completed_state<>CASE WHEN owner.completed_count>1 OR owner.reference_count>16 OR owner.authority_count>1 THEN 'inconsistent'
                                                 WHEN owner.completed_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR metadata.failed_state<>CASE WHEN owner.reference_count>16 OR owner.authority_count>1 THEN 'inconsistent' ELSE 'not_observed' END
              OR metadata.exit_state<>'source_unsupported' OR metadata.exit_code IS NOT NULL
              OR metadata.mcp_server_identity_state<>'not_observed' OR metadata.mcp_server_identity IS NOT NULL
              OR metadata.mcp_server_name_state<>'source_unsupported' OR metadata.mcp_server_name IS NOT NULL
              OR metadata.mcp_tool_name_state<>'not_observed' OR metadata.mcp_tool_name IS NOT NULL
              OR metadata.retry_state<>'not_observed' OR metadata.recovery_state<>'not_observed'
              OR metadata.child_activity_state<>'not_observed' OR metadata.child_activity_count IS NOT NULL
              OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)
                   <>CASE WHEN owner.reference_count>16 THEN 16 ELSE owner.reference_count END
              OR EXISTS(SELECT 1 FROM expected_references expected_reference
                    WHERE expected_reference.carrier_digest=owner.carrier_digest AND expected_reference.source_ordinal<16
                      AND NOT EXISTS(SELECT 1 FROM local_workspace_node_source_references reference
                        WHERE reference.node_id=node.node_id AND reference.source_ordinal=expected_reference.source_ordinal
                          AND reference.source_kind='session_event' AND reference.source_identity=expected_reference.event_id
                          AND reference.trace_id IS NULL AND reference.span_id IS NULL AND reference.event_id=expected_reference.event_id
                          AND reference.revision_input=expected_reference.revision_input))
            )
        ),
        missing_persisted AS (
          SELECT 1 FROM expected owner
          WHERE owner.session_id=$session_id AND owner.session_count=1 AND owner.run_count=1 AND owner.authority_count=1
            AND NOT EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE node.session_id=$session_id AND receipt.semantic_kind='tool' AND receipt.source_family='session_sdk'
                AND receipt.carrier_digest=owner.carrier_digest)
        )
        SELECT EXISTS(SELECT 1 FROM invalid_persisted UNION ALL SELECT 1 FROM missing_persisted);
        """;

    private const string SdkSubagentOwnerValidationSql = """
        WITH candidates AS (
          SELECT local_workspace_semantic_digest('session_sdk_subagent',native.native_session_id,run.native_run_id) carrier_digest,
                 event.session_id,event.run_id,event.event_id,event.source_adapter,event.source_event_id,event.type,event.occurred_at,
                 event.trace_id,event.source_adapter||'|native_run|v1' authority_receipt
          FROM session_events event
          JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
          JOIN session_native_ids native ON native.session_id=event.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
          WHERE event.session_id=$session_id AND event.source_adapter='copilot-sdk-stream' COLLATE BINARY
            AND event.source_surface='copilot-sdk' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
            AND event.type IN ('subagent.selected','subagent.started','subagent.completed','subagent.failed','subagent.deselected')
            AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
            AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=event.session_id
              AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
            AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=event.session_id
              AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
        ),
        groups AS (
          SELECT carrier_digest,MIN(session_id) session_id,MIN(run_id) run_id,
                 COUNT(DISTINCT session_id) session_count,COUNT(DISTINCT run_id) run_count,
                 COUNT(DISTINCT authority_receipt) authority_count,MIN(authority_receipt) authority_receipt,
                 COUNT(DISTINCT event_id) reference_count,
                 SUM(type='subagent.selected') selected_count,SUM(type='subagent.started') started_count,
                 SUM(type='subagent.completed') completed_count,SUM(type='subagent.failed') failed_count,
                 SUM(type='subagent.deselected') deselected_count,
                 SUM(trace_id IS NOT NULL) technical_identity_count
          FROM candidates GROUP BY carrier_digest
        ),
        expected_references AS (
          SELECT candidate.carrier_digest,candidate.event_id,candidate.source_adapter,candidate.source_event_id,
                 candidate.source_adapter||'|'||candidate.source_event_id||'|'||candidate.type||'|'||candidate.type||'|1|'||
                   COALESCE(candidate.occurred_at,'')||'|'||candidate.authority_receipt revision_input,
                 row_number() OVER(PARTITION BY candidate.carrier_digest ORDER BY candidate.event_id COLLATE BINARY)-1 source_ordinal
          FROM candidates candidate
        ),
        invalid_persisted AS (
          SELECT 1
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          LEFT JOIN groups owner ON owner.carrier_digest=receipt.carrier_digest
          LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
          LEFT JOIN local_workspace_subagent_lifecycle lifecycle ON lifecycle.node_id=node.node_id
          WHERE node.session_id=$session_id AND receipt.semantic_kind='subagent' AND receipt.source_family='session_sdk'
            AND (
              owner.carrier_digest IS NULL OR owner.session_count<>1 OR owner.run_count<>1 OR owner.authority_count<>1
              OR owner.reference_count>4096
              OR receipt.scope_kind<>'native_run' OR receipt.authority_receipt<>owner.authority_receipt
              OR node.source_kind<>'semantic_subagent' OR node.kind<>'subagent' OR node.source_identity<>owner.carrier_digest
              OR node.node_id<>local_workspace_node_id('semantic_subagent',owner.carrier_digest)
              OR node.session_id<>owner.session_id OR node.execution_id<>local_workspace_execution_id('session_run',owner.run_id)
              OR execution.session_id<>owner.session_id OR execution.source_kind<>'session_run' OR execution.source_identity<>owner.run_id
              OR node.trace_id IS NOT NULL OR node.span_id IS NOT NULL OR node.event_id IS NOT NULL
              OR node.parent_node_id<>local_workspace_node_id('execution_root',owner.run_id) OR node.relationship_authority<>'exact'
              OR node.name_state<>'not_observed' OR node.name_text IS NOT NULL OR node.lifecycle<>'unknown' OR node.status<>'unknown'
              OR node.time_authority<>'missing' OR node.start_utc_ticks IS NOT NULL OR node.end_utc_ticks IS NOT NULL OR node.duration_ms IS NOT NULL
              OR lifecycle.node_id IS NULL
              OR lifecycle.selected_state<>CASE WHEN owner.selected_count>1 OR owner.selected_count>0 AND (owner.reference_count>16 OR owner.authority_count>1) THEN 'inconsistent'
                                                 WHEN owner.selected_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR lifecycle.started_state<>CASE WHEN owner.started_count>1 OR owner.started_count>0 AND (owner.reference_count>16 OR owner.authority_count>1) THEN 'inconsistent'
                                                WHEN owner.started_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR lifecycle.completed_state<>CASE WHEN owner.completed_count>1 OR owner.completed_count>0 AND (owner.reference_count>16 OR owner.authority_count>1) THEN 'inconsistent'
                                                  WHEN owner.completed_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR lifecycle.failed_state<>CASE WHEN owner.failed_count>1 OR owner.failed_count>0 AND (owner.reference_count>16 OR owner.authority_count>1) THEN 'inconsistent'
                                               WHEN owner.failed_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR lifecycle.deselected_state<>CASE WHEN owner.deselected_count>1 OR owner.deselected_count>0 AND (owner.reference_count>16 OR owner.authority_count>1) THEN 'inconsistent'
                                                   WHEN owner.deselected_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR lifecycle.input_state<>'source_unsupported'
              OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)
                   <>CASE WHEN owner.reference_count>16 THEN 16 ELSE owner.reference_count END
              OR EXISTS(SELECT 1 FROM expected_references expected_reference
                    WHERE expected_reference.carrier_digest=owner.carrier_digest AND expected_reference.source_ordinal<16
                      AND NOT EXISTS(SELECT 1 FROM local_workspace_node_source_references reference
                        WHERE reference.node_id=node.node_id AND reference.source_ordinal=expected_reference.source_ordinal
                          AND reference.source_kind='session_event' AND reference.source_identity=expected_reference.event_id
                          AND reference.trace_id IS NULL AND reference.span_id IS NULL AND reference.event_id=expected_reference.event_id
                          AND reference.revision_input=expected_reference.revision_input))
            )
        ),
        missing_persisted AS (
          SELECT 1 FROM groups owner
          WHERE owner.session_id=$session_id AND owner.session_count=1 AND owner.run_count=1 AND owner.authority_count=1
            AND NOT EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE node.session_id=$session_id AND receipt.semantic_kind='subagent' AND receipt.source_family='session_sdk'
                AND receipt.carrier_digest=owner.carrier_digest)
        )
        SELECT EXISTS(SELECT 1 FROM invalid_persisted UNION ALL SELECT 1 FROM missing_persisted);
        """;

    internal const string ClaudeHookOwnerValidationSql = """
        WITH owners AS (
          SELECT receipt.*,node.session_id,node.execution_id,node.source_kind,node.kind,node.name_state,node.name_text,
                 node.lifecycle,node.status,node.parent_node_id,node.relationship_authority,
                 execution.source_identity run_id,run.native_run_id,native.native_session_id
          FROM local_workspace_semantic_receipts receipt
          JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
          JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id AND execution.session_id=node.session_id
          JOIN session_runs run ON run.run_id=execution.source_identity AND run.session_id=node.session_id AND run.source_surface='claude-code' COLLATE BINARY
          JOIN session_native_ids native ON native.session_id=node.session_id AND native.source_surface='claude-code' COLLATE BINARY
          WHERE node.session_id=$session_id AND receipt.source_family='claude_hook'),
        facts AS (
          SELECT owner.*,COUNT(reference.source_ordinal) reference_count,
                 SUM(event.type='PreToolUse') tool_started_count,SUM(event.type='PostToolUse') tool_completed_count,
                 SUM(event.type='PostToolUseFailure') tool_failed_count,SUM(event.type='SubagentStart') subagent_started_count,
                 SUM(event.type='SubagentStop') subagent_completed_count,
                 SUM(substr(reference.revision_input,-length('|claude-subagent-name-v1:unavailable|'||owner.authority_receipt))
                   ='|claude-subagent-name-v1:unavailable|'||owner.authority_receipt) subagent_name_unavailable_count,
                 SUM(substr(reference.revision_input,-length('|'||local_workspace_claude_agent_type_marker(owner.name_text)||'|'||owner.authority_receipt))
                   ='|'||local_workspace_claude_agent_type_marker(owner.name_text)||'|'||owner.authority_receipt) subagent_name_text_match_count,
                 COUNT(DISTINCT CASE WHEN
                   substr(reference.revision_input,-length('|'||owner.authority_receipt))='|'||owner.authority_receipt
                   AND substr(reference.revision_input,-(length('claude-subagent-name-v1:recorded:')+64+length(owner.authority_receipt)+1),length('claude-subagent-name-v1:recorded:'))='claude-subagent-name-v1:recorded:'
                   AND substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64)=lower(substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64))
                   AND substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64) NOT GLOB '*[^0-9a-f]*'
                   THEN substr(reference.revision_input,-(length('claude-subagent-name-v1:recorded:')+64+length(owner.authority_receipt)+1),length('claude-subagent-name-v1:recorded:')+64) END) subagent_name_digest_count,
                 SUM(substr(reference.revision_input,-length('|claude-subagent-name-v1:unavailable|'||owner.authority_receipt))
                   ='|claude-subagent-name-v1:unavailable|'||owner.authority_receipt
                   OR substr(reference.revision_input,-length('|'||owner.authority_receipt))='|'||owner.authority_receipt
                     AND substr(reference.revision_input,-(length('claude-subagent-name-v1:recorded:')+64+length(owner.authority_receipt)+1),length('claude-subagent-name-v1:recorded:'))='claude-subagent-name-v1:recorded:'
                     AND substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64)=lower(substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64))
                     AND substr(reference.revision_input,-(64+length(owner.authority_receipt)+1),64) NOT GLOB '*[^0-9a-f]*') subagent_name_marker_count,
                 SUM(reference.source_kind<>'session_event' OR reference.source_identity<>event.event_id OR reference.event_id<>event.event_id
                   OR reference.trace_id IS NOT NULL OR reference.span_id IS NOT NULL
                   OR event.session_id<>owner.session_id OR event.run_id<>owner.run_id
                   OR event.source_surface<>'claude-code' COLLATE BINARY OR event.source_adapter<>'claude-code-hook' COLLATE BINARY
                   OR event.adapter_version IS NULL OR trim(event.adapter_version)=''
                   OR event.normalization_version IS NULL OR trim(event.normalization_version)=''
                   OR NOT (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
                     OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                       AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')) invalid_reference_count
          FROM owners owner
          LEFT JOIN local_workspace_node_source_references reference ON reference.node_id=owner.node_id
          LEFT JOIN session_events event ON event.event_id=reference.event_id
          GROUP BY owner.node_id),
        invalid AS (
          SELECT 1 FROM facts owner
          LEFT JOIN local_workspace_tool_metadata tool ON tool.node_id=owner.node_id
          LEFT JOIN local_workspace_subagent_lifecycle subagent ON subagent.node_id=owner.node_id
          WHERE (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=owner.session_id AND binding.source_surface='claude-code' COLLATE BINARY)<>1
            OR (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=owner.session_id AND candidate.source_surface='claude-code' COLLATE BINARY AND candidate.native_run_id=owner.native_run_id COLLATE BINARY)<>1
            OR owner.invalid_reference_count<>0 OR owner.reference_count<>2
            OR owner.parent_node_id<>local_workspace_node_id('execution_root',owner.run_id) OR owner.relationship_authority<>'exact'
            OR owner.semantic_kind='tool' AND (
              owner.scope_kind<>'native_session' OR owner.authority_receipt<>'claude-code-hook|exact_hook_tool|v1'
              OR owner.source_kind<>'semantic_tool' OR owner.kind<>'tool'
              OR owner.node_id<>local_workspace_node_id('semantic_tool',owner.carrier_digest)
              OR owner.tool_started_count<>1 OR owner.tool_completed_count+owner.tool_failed_count<>1
              OR owner.tool_completed_count>0 AND owner.tool_failed_count>0
              OR owner.name_state NOT IN ('recorded','not_observed') OR owner.name_state='recorded' AND owner.name_text IS NULL
              OR owner.name_state='not_observed' AND owner.name_text IS NOT NULL
              OR owner.lifecycle<>CASE WHEN owner.tool_completed_count=1 THEN 'completed' ELSE 'failed' END
              OR owner.status<>CASE WHEN owner.tool_completed_count=1 THEN 'completed' ELSE 'failed' END
              OR tool.node_id IS NULL OR tool.started_state<>'recorded'
              OR tool.completed_state<>CASE WHEN owner.tool_completed_count=1 THEN 'recorded' ELSE 'not_observed' END
              OR tool.failed_state<>CASE WHEN owner.tool_failed_count=1 THEN 'recorded' ELSE 'not_observed' END)
            OR owner.semantic_kind='subagent' AND (
              owner.scope_kind<>'native_run' OR owner.authority_receipt<>'claude-code-hook|exact_hook_subagent|v1'
              OR owner.source_kind<>'semantic_subagent' OR owner.kind<>'subagent'
              OR owner.carrier_digest<>local_workspace_semantic_digest('claude_hook_subagent',owner.native_session_id,owner.native_run_id)
              OR owner.node_id<>local_workspace_node_id('semantic_subagent',owner.carrier_digest)
              OR owner.subagent_started_count<>1 OR owner.subagent_completed_count<>1
              OR owner.subagent_name_marker_count<>owner.reference_count
              OR owner.name_state='recorded' AND (owner.name_text IS NULL OR owner.subagent_name_text_match_count<>owner.reference_count OR owner.subagent_name_digest_count<>1)
              OR owner.name_state='not_observed' AND (owner.name_text IS NOT NULL
                OR owner.subagent_name_unavailable_count=0 AND owner.subagent_name_digest_count=1)
              OR owner.name_state NOT IN ('recorded','not_observed')
              OR owner.lifecycle<>'unknown' OR owner.status<>'unknown'
              OR subagent.node_id IS NULL OR subagent.selected_state<>'not_observed' OR subagent.started_state<>'recorded'
              OR subagent.completed_state<>'recorded' OR subagent.failed_state<>'not_observed'
              OR subagent.deselected_state<>'not_observed' OR subagent.input_state<>'source_unsupported')),
        expected_subagent AS (
          SELECT local_workspace_semantic_digest('claude_hook_subagent',native.native_session_id,run.native_run_id) carrier_digest
          FROM session_events event
          JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id AND run.source_surface='claude-code' COLLATE BINARY
          JOIN session_native_ids native ON native.session_id=event.session_id AND native.source_surface='claude-code' COLLATE BINARY
          WHERE event.session_id=$session_id AND event.source_surface='claude-code' COLLATE BINARY
            AND event.source_adapter='claude-code-hook' COLLATE BINARY AND event.type IN ('SubagentStart','SubagentStop')
            AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
            AND event.adapter_version IS NOT NULL AND trim(event.adapter_version)<>''
            AND event.normalization_version IS NOT NULL AND trim(event.normalization_version)<>''
            AND (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
              OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint) AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')
            AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=event.session_id AND binding.source_surface='claude-code' COLLATE BINARY)=1
            AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=event.session_id AND candidate.source_surface='claude-code' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
          GROUP BY carrier_digest HAVING SUM(event.type='SubagentStart')=1 AND SUM(event.type='SubagentStop')=1),
        missing AS (
          SELECT 1 FROM expected_subagent expected WHERE NOT EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
            JOIN local_workspace_nodes node ON node.node_id=receipt.node_id WHERE node.session_id=$session_id AND receipt.source_family='claude_hook' AND receipt.semantic_kind='subagent' AND receipt.carrier_digest=expected.carrier_digest))
        SELECT EXISTS(SELECT 1 FROM invalid UNION ALL SELECT 1 FROM missing);
        """;

    private static async Task ValidateCurrentSkillOwnerGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset acceptedAt,
        SkillProjectionCurrentInvocationProjection? projection,
        string registryIdentity,
        CancellationToken token)
    {
        if (projection is null)
        {
            using var persisted = Command(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM local_workspace_nodes WHERE session_id=$session_id AND source_kind='skill_invocation');",
                sessionId);
            if (Convert.ToInt64(await persisted.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) == 0)
                return;
        }
        var expected = projection?.State is "current" or "certification_pending"
            ? projection.Invocations.ToDictionary(
                invocation => LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity),
                StringComparer.Ordinal)
            : new Dictionary<string, SkillProjectionCanonicalInvocation>(StringComparer.Ordinal);
        var hasSdkOwner = TableExists(connection, transaction, "skill_invocation_snapshots");
        await ValidateCurrentSkillNodeFacts(connection, transaction, sessionId, expected.Values, hasSdkOwner, token);
        var sdkOwnerExists = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN EXISTS(
                SELECT 1 FROM skill_projection_sdk_claims claim
                JOIN skill_invocation_snapshots snapshot
                  ON snapshot.claim_id=claim.claim_id AND snapshot.session_id=claim.session_id AND snapshot.event_id=claim.event_id
                 AND snapshot.name=claim.skill_name AND snapshot.source IS claim.skill_source
                 AND snapshot.trigger IS claim.invocation_trigger
                JOIN session_events event ON event.event_id=snapshot.event_id AND event.session_id=snapshot.session_id
                 AND event.source_adapter=claim.source_adapter AND event.source_event_id=claim.source_event_id
                 AND event.source_surface=claim.source_surface AND event.source_application_version=claim.source_application_version
                 AND event.adapter_version=claim.adapter_version AND event.normalization_version=claim.normalization_version
                 AND event.schema_fingerprint=claim.schema_fingerprint
                WHERE 'sdk:'||claim.claim_id=r.source_identity AND claim.session_id=n.session_id
                  AND snapshot.event_id=r.event_id AND r.trace_id IS NULL AND r.span_id IS NULL) ELSE 0 END
              """
            : "0";
        var sdkOwnerSource = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN (
                SELECT snapshot.source FROM skill_invocation_snapshots snapshot
                WHERE 'sdk:'||snapshot.claim_id=r.source_identity AND snapshot.session_id=n.session_id AND snapshot.event_id=r.event_id)
              """
            : "CASE WHEN 0 THEN NULL";
        var sdkOwnerTrigger = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN (
                SELECT snapshot.trigger FROM skill_invocation_snapshots snapshot
                WHERE 'sdk:'||snapshot.claim_id=r.source_identity AND snapshot.session_id=n.session_id AND snapshot.event_id=r.event_id)
              """
            : "CASE WHEN 0 THEN NULL";
        var sdkOwnerHistorical = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN (
                SELECT snapshot.snapshot_id FROM skill_invocation_snapshots snapshot
                WHERE 'sdk:'||snapshot.claim_id=r.source_identity AND snapshot.session_id=n.session_id AND snapshot.event_id=r.event_id) END
              """
            : "NULL";
        var sdkOwnerProducerTrace = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN (
                SELECT claim.producer_trace_id FROM skill_projection_sdk_claims claim
                JOIN skill_invocation_snapshots snapshot
                  ON snapshot.claim_id=claim.claim_id AND snapshot.session_id=claim.session_id
                 AND snapshot.event_id=claim.event_id
                WHERE 'sdk:'||claim.claim_id=r.source_identity AND claim.session_id=n.session_id
                  AND snapshot.event_id=r.event_id) END
              """
            : "NULL";
        var sdkOwnerProducerSpan = hasSdkOwner
            ? """
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN (
                SELECT claim.producer_span_id FROM skill_projection_sdk_claims claim
                JOIN skill_invocation_snapshots snapshot
                  ON snapshot.claim_id=claim.claim_id AND snapshot.session_id=claim.session_id
                 AND snapshot.event_id=claim.event_id
                WHERE 'sdk:'||claim.claim_id=r.source_identity AND claim.session_id=n.session_id
                  AND snapshot.event_id=r.event_id) END
              """
            : "NULL";
        using var command = Command(connection, transaction, $"""
            SELECT n.node_id,n.source_identity,m.current_valid_state,
              m.source_state,m.source,m.trigger_state,m.trigger,
              m.inventory_reference_state,m.inventory_reference,
              m.historical_snapshot_reference_state,m.historical_snapshot_reference,m.registry_generation_identity,
              n.skill_activity_state,n.skill_activity_count,
              r.source_kind,r.source_ordinal,r.source_identity,r.trace_id,r.span_id,r.event_id,r.revision_input,
              CASE WHEN r.source_identity LIKE 'otel:%' THEN EXISTS(
                SELECT 1 FROM skill_projection_invocations i
                WHERE 'otel:'||CAST(i.raw_record_id AS TEXT)||':'||CAST(i.span_ordinal AS TEXT)=r.source_identity
                  AND i.session_id=n.session_id AND i.trace_id IS r.trace_id AND i.span_id IS r.span_id
                  AND EXISTS(SELECT 1 FROM session_events e WHERE e.event_id=r.event_id AND e.session_id=n.session_id
                    AND e.source_adapter='otel-exact' COLLATE BINARY AND e.trace_id=r.trace_id COLLATE BINARY
                    AND e.type='otel.span' COLLATE BINARY
                    AND e.source_event_id=r.trace_id||'/'||r.span_id COLLATE BINARY)) ELSE 0 END,
              {sdkOwnerExists},
              CASE WHEN r.source_identity LIKE 'sdk:%' THEN EXISTS(
                SELECT 1 FROM retention_items i
                LEFT JOIN retention_tombstones t ON t.item_id=i.item_id
                WHERE i.store_kind='session_event_content' AND i.source_item_id=r.event_id
                  AND t.item_id IS NULL AND i.deleted_at IS NULL AND i.read_denied_at IS NULL AND i.error_code IS NULL
                  AND (i.state='retained_by_policy' OR i.state='expiring' AND i.expires_at>$now)) ELSE 0 END,
              {sdkOwnerSource}
                   WHEN r.source_identity LIKE 'otel:%' THEN (
                     SELECT invocation.skill_source FROM skill_projection_invocations invocation
                     WHERE 'otel:'||CAST(invocation.raw_record_id AS TEXT)||':'||CAST(invocation.span_ordinal AS TEXT)=r.source_identity
                       AND invocation.session_id=n.session_id AND invocation.trace_id IS r.trace_id AND invocation.span_id IS r.span_id)
                   END,
              {sdkOwnerTrigger}
                   WHEN r.source_identity LIKE 'otel:%' THEN (
                     SELECT invocation.invocation_trigger FROM skill_projection_invocations invocation
                     WHERE 'otel:'||CAST(invocation.raw_record_id AS TEXT)||':'||CAST(invocation.span_ordinal AS TEXT)=r.source_identity
                       AND invocation.session_id=n.session_id AND invocation.trace_id IS r.trace_id AND invocation.span_id IS r.span_id)
                   END,
              {sdkOwnerHistorical},
              {sdkOwnerProducerTrace},
              {sdkOwnerProducerSpan}
            FROM local_workspace_nodes n
            JOIN local_workspace_skill_metadata m ON m.node_id=n.node_id
            LEFT JOIN local_workspace_node_source_references r ON r.node_id=n.node_id
            WHERE n.session_id=$session_id AND n.source_kind='skill_invocation'
            ORDER BY n.node_id,r.source_ordinal;
            """, sessionId);
        command.Parameters.AddWithValue("$now", Canonical(acceptedAt));
        var rows = new Dictionary<string, PersistedSkillProof>(StringComparer.Ordinal);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var nodeId = reader.GetString(0);
                if (!rows.TryGetValue(nodeId, out var proof))
                {
                    proof = new(nodeId, reader.GetString(1), reader.GetString(2), reader.GetString(3), S(reader, 4), reader.GetString(5), S(reader, 6),
                        reader.GetString(7), S(reader, 8), reader.GetString(9), S(reader, 10), reader.GetString(11), reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13), []);
                    rows.Add(nodeId, proof);
                }
                if (!reader.IsDBNull(14))
                    proof.References.Add(new(reader.GetString(14), reader.GetInt64(15), S(reader, 16), S(reader, 17), S(reader, 18), S(reader, 19),
                        S(reader, 20), reader.GetInt64(21) != 0, reader.GetInt64(22) != 0, reader.GetInt64(23) != 0,
                        S(reader, 24), S(reader, 25), S(reader, 26), S(reader, 27), S(reader, 28)));
            }
        }

        foreach (var proof in rows.Values)
        {
            if (!string.Equals(proof.RegistryIdentity, registryIdentity, StringComparison.Ordinal))
                RejectSkillOwnerGraph();
            var persistedRevision = PersistedSkillReferenceRevision(proof);
            if (persistedRevision is null || proof.References.Any(reference =>
                    !string.Equals(reference.RevisionInput, persistedRevision, StringComparison.Ordinal)))
                RejectSkillOwnerGraph();
            if (!expected.TryGetValue(proof.NodeId, out var invocation))
            {
                var persistedValidState = PersistedSkillValidState(proof);
                if (proof.References.Count == 0 || proof.References.Any(reference =>
                        !reference.LegitimatelyInactive || !InactiveSkillReferenceMatchesCanonical(reference, proof.CanonicalIdentity, null))
                    || persistedValidState is null
                    || !MetadataMatchesPersistedOwners(proof, persistedValidState))
                    RejectSkillOwnerGraph();
                continue;
            }
            if (!string.Equals(proof.CanonicalIdentity, invocation.CanonicalIdentity, StringComparison.Ordinal))
                RejectSkillOwnerGraph();
            var revision = SkillReferenceRevision(invocation);
            var expectedReferences = new List<PersistedSkillReference>(2);
            if (invocation.OtelSourceIdentity is not null)
                expectedReferences.Add(new("skill_claim", 0, invocation.OtelSourceIdentity, invocation.ProducerTraceId,
                    invocation.ProducerSpanId, invocation.OtelCarrierEventId, revision, true, false, false));
            if (invocation.SdkSourceIdentity is not null)
                expectedReferences.Add(new("skill_claim", invocation.OtelSourceIdentity is null ? 0 : 1, invocation.SdkSourceIdentity, null, null,
                    invocation.SdkCarrierEventId, revision, false, true, true));
            var ownerTransition = false;
            var active = new List<PersistedSkillReference>();
            foreach (var reference in proof.References)
            {
                if (expectedReferences.Any(expectedReference => SameSkillReference(reference, expectedReference)))
                    active.Add(reference);
                else if (reference.LegitimatelyInactive
                    && InactiveSkillReferenceMatchesCanonical(reference, proof.CanonicalIdentity, invocation))
                    ownerTransition = true;
                else
                    RejectSkillOwnerGraph();
            }
            if (active.Count != expectedReferences.Count
                || expectedReferences.Any(expectedReference => !active.Any(reference => SameSkillReference(reference, expectedReference))))
                RejectSkillOwnerGraph();
            if (ownerTransition
                    ? !MetadataMatchesPersistedOwners(proof, invocation.CurrentValidState)
                    : !MetadataMatches(proof, invocation))
                RejectSkillOwnerGraph();
        }
    }

    private static async Task ValidateCurrentSkillNodeFacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        IEnumerable<SkillProjectionCanonicalInvocation> invocations,
        bool hasSdkOwner,
        CancellationToken token)
    {
        var skills = invocations.Select(static invocation => new
        {
            identity = invocation.CanonicalIdentity,
            execution_kind = invocation.ExecutionSourceKind,
            execution_identity = invocation.ExecutionSourceIdentity,
            sdk_parent = invocation.SdkSourceParentEventId,
            sdk_adapter = invocation.SdkSourceAdapter,
            sdk_event = invocation.SdkCarrierEventId,
            otel_event = invocation.OtelCarrierEventId,
            name = invocation.SdkSkillName ?? invocation.OtelSkillName,
            trace = invocation.ProducerTraceId,
            span = invocation.ProducerSpanId,
            otel = invocation.OtelSourceIdentity,
            sdk = invocation.SdkSourceIdentity,
            state = invocation.CurrentValidState,
        }).ToArray();
        if (skills.Length == 0) return;
        var persistedSdkParent = hasSdkOwner
            ? """
              (SELECT snapshot.source_parent_event_id FROM local_workspace_node_source_references reference
               JOIN skill_invocation_snapshots snapshot ON 'sdk:'||snapshot.claim_id=reference.source_identity
                 AND snapshot.session_id=$session_id AND snapshot.event_id=reference.event_id
               WHERE reference.node_id=persisted.node_id AND reference.source_kind='skill_claim'
                 AND reference.source_identity LIKE 'sdk:%' LIMIT 1)
              """
            : "NULL";
        var persistedSdkAdapter = hasSdkOwner
            ? """
              (SELECT claim.source_adapter FROM local_workspace_node_source_references reference
               JOIN skill_projection_sdk_claims claim ON 'sdk:'||claim.claim_id=reference.source_identity
                 AND claim.session_id=$session_id AND claim.event_id=reference.event_id
               WHERE reference.node_id=persisted.node_id AND reference.source_kind='skill_claim'
                 AND reference.source_identity LIKE 'sdk:%' LIMIT 1)
              """
            : "NULL";
        var persistedSdkName = hasSdkOwner
            ? """
              (SELECT snapshot.name FROM local_workspace_node_source_references reference
               JOIN skill_invocation_snapshots snapshot ON 'sdk:'||snapshot.claim_id=reference.source_identity
                 AND snapshot.session_id=$session_id AND snapshot.event_id=reference.event_id
               WHERE reference.node_id=persisted.node_id AND reference.source_kind='skill_claim'
                 AND reference.source_identity LIKE 'sdk:%' LIMIT 1)
              """
            : "NULL";
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        using var command = Command(connection, transaction, $"""
            WITH canonical AS (
              SELECT value->>'identity' canonical_identity,value->>'execution_kind' execution_kind,
                     value->>'execution_identity' execution_identity,value->>'sdk_parent' sdk_parent,
                     value->>'sdk_adapter' sdk_adapter,value->>'sdk_event' sdk_event,value->>'otel_event' otel_event,
                     value->>'name' name,value->>'trace' trace_id,value->>'span' span_id,
                      value->>'otel' otel_source_identity,value->>'sdk' sdk_source_identity,value->>'state' current_valid_state,
                     row_number() OVER(PARTITION BY value->>'execution_kind',value->>'execution_identity'
                                       ORDER BY value->>'identity' COLLATE BINARY) skill_ordinal
              FROM json_each($skills)),
            core_counts AS (
              SELECT execution_id,COUNT(*) core_count FROM local_workspace_nodes
              WHERE session_id=$session_id AND source_kind<>'skill_invocation'
              GROUP BY execution_id),
            persisted AS (
              SELECT c.*,local_workspace_node_id('skill_invocation',c.canonical_identity) node_id,
                     (SELECT reference.source_identity FROM local_workspace_node_source_references reference
                      WHERE reference.node_id=local_workspace_node_id('skill_invocation',c.canonical_identity)
                        AND reference.source_kind='skill_claim' AND reference.source_identity LIKE 'otel:%' LIMIT 1)
                       persisted_otel_source_identity,
                     (SELECT reference.source_identity FROM local_workspace_node_source_references reference
                      WHERE reference.node_id=local_workspace_node_id('skill_invocation',c.canonical_identity)
                        AND reference.source_kind='skill_claim' AND reference.source_identity LIKE 'sdk:%' LIMIT 1)
                       persisted_sdk_source_identity,
                     (SELECT reference.event_id FROM local_workspace_node_source_references reference
                      WHERE reference.node_id=local_workspace_node_id('skill_invocation',c.canonical_identity)
                        AND reference.source_kind='skill_claim' AND reference.source_identity LIKE 'otel:%' LIMIT 1)
                       persisted_otel_event,
                     (SELECT reference.event_id FROM local_workspace_node_source_references reference
                      WHERE reference.node_id=local_workspace_node_id('skill_invocation',c.canonical_identity)
                        AND reference.source_kind='skill_claim' AND reference.source_identity LIKE 'sdk:%' LIMIT 1)
                       persisted_sdk_event,
                     (SELECT invocation.skill_name FROM local_workspace_node_source_references reference
                      JOIN skill_projection_invocations invocation
                        ON 'otel:'||CAST(invocation.raw_record_id AS TEXT)||':'||CAST(invocation.span_ordinal AS TEXT)=reference.source_identity
                       AND invocation.session_id=$session_id AND invocation.trace_id IS reference.trace_id
                       AND invocation.span_id IS reference.span_id
                      WHERE reference.node_id=local_workspace_node_id('skill_invocation',c.canonical_identity)
                        AND reference.source_kind='skill_claim' AND reference.source_identity LIKE 'otel:%' LIMIT 1)
                       persisted_otel_name
              FROM canonical c),
            expected_rows AS (
              SELECT persisted.*,h.execution_id,COALESCE(counts.core_count,0)+persisted.skill_ordinal expected_ordinal,
                     CASE WHEN {persistedSdkParent} IS NULL THEN local_workspace_node_id('execution_root',h.source_identity)
                          WHEN (SELECT COUNT(*) FROM session_events p
                                WHERE p.session_id=$session_id AND p.run_id=persisted.execution_identity
                                  AND p.source_event_id={persistedSdkParent})=1
                           AND EXISTS(SELECT 1 FROM session_events p
                                      WHERE p.session_id=$session_id AND p.run_id=persisted.execution_identity
                                        AND p.source_adapter={persistedSdkAdapter}
                                        AND p.source_event_id={persistedSdkParent})
                            THEN local_workspace_node_id('session_event',(SELECT p.event_id FROM session_events p
                                 WHERE p.session_id=$session_id AND p.run_id=persisted.execution_identity
                                   AND p.source_adapter={persistedSdkAdapter}
                                   AND p.source_event_id={persistedSdkParent}))
                          ELSE local_workspace_node_id('unknown_relation_group',h.source_identity) END expected_parent,
                     CASE WHEN {persistedSdkParent} IS NULL THEN 'exact'
                          WHEN (SELECT COUNT(*) FROM session_events p
                                WHERE p.session_id=$session_id AND p.run_id=persisted.execution_identity
                                  AND p.source_event_id={persistedSdkParent})=1
                           AND EXISTS(SELECT 1 FROM session_events p
                                      WHERE p.session_id=$session_id AND p.run_id=persisted.execution_identity
                                        AND p.source_adapter={persistedSdkAdapter}
                                        AND p.source_event_id={persistedSdkParent})
                            THEN 'explicit' ELSE 'unknown' END expected_relationship,
                     COALESCE((SELECT occurred_at FROM session_events WHERE session_id=$session_id AND event_id=persisted.persisted_sdk_event),
                              (SELECT occurred_at FROM session_events WHERE session_id=$session_id AND event_id=persisted.persisted_otel_event)) occurred_at,
                     COALESCE(persisted.persisted_sdk_event,persisted.persisted_otel_event) expected_event,
                     COALESCE({persistedSdkName},persisted.persisted_otel_name) expected_name
              FROM persisted persisted
              LEFT JOIN local_workspace_execution_headers h ON h.session_id=$session_id
                AND h.source_kind=persisted.execution_kind AND h.source_identity=persisted.execution_identity
              LEFT JOIN core_counts counts ON counts.execution_id=h.execution_id),
            expected AS (
              SELECT rows.*,
                     CASE WHEN local_workspace_ticks(occurred_at) IS NULL
                          THEN CASE WHEN occurred_at IS NULL THEN 'missing' ELSE 'invalid' END ELSE 'recorded' END expected_time,
                     local_workspace_ticks(occurred_at) expected_ticks
              FROM expected_rows rows),
            invalid AS (
              SELECT 1 FROM expected e
               LEFT JOIN local_workspace_nodes n
                 ON n.session_id=$session_id AND n.node_id=local_workspace_node_id('skill_invocation',e.canonical_identity)
               LEFT JOIN local_workspace_skill_metadata metadata ON metadata.node_id=n.node_id
               WHERE n.node_id IS NULL
                   OR n.node_id IS NOT NULL AND (e.execution_id IS NULL
                  OR metadata.node_id IS NULL
                  OR NOT (metadata.current_valid_state IS e.current_valid_state
                    OR e.current_valid_state='current' AND metadata.current_valid_state='certification_pending')
                  OR metadata.inventory_reference_state<>'unavailable' OR metadata.inventory_reference IS NOT NULL
                  OR n.execution_id IS NOT e.execution_id
                 OR n.source_ordinal IS NOT e.expected_ordinal
                 OR n.parent_node_id IS NOT e.expected_parent
                 OR n.relationship_authority IS NOT e.expected_relationship
                 OR n.kind<>'skill'
                 OR n.name_state IS NOT CASE WHEN e.expected_name IS NULL OR trim(e.expected_name)=''
                                              THEN 'invalid' ELSE 'recorded' END
                 OR n.name_text IS NOT CASE WHEN e.expected_name IS NULL OR trim(e.expected_name)=''
                                            THEN NULL ELSE e.expected_name END
                 OR n.lifecycle<>'completed' OR n.status<>'completed'
                 OR n.time_authority IS NOT e.expected_time
                 OR n.start_utc_ticks IS NOT e.expected_ticks OR n.end_utc_ticks IS NOT e.expected_ticks
                 OR n.duration_ms IS NOT CASE WHEN e.expected_ticks IS NULL THEN NULL ELSE 0 END
                 OR n.trace_id IS NOT e.trace_id OR n.span_id IS NOT e.span_id
                 OR n.event_id IS NOT e.expected_event
                 OR n.otel_source_identity IS NOT e.persisted_otel_source_identity
                 OR n.sdk_source_identity IS NOT e.persisted_sdk_source_identity
                 OR NOT (n.skill_activity_state IS CASE e.current_valid_state
                      WHEN 'current' THEN 'recorded' ELSE 'certification_pending' END
                    AND n.skill_activity_count IS CASE e.current_valid_state WHEN 'current' THEN 1 END
                    OR e.current_valid_state='current' AND metadata.current_valid_state='certification_pending'
                      AND n.skill_activity_state='certification_pending' AND n.skill_activity_count IS NULL)
                 OR n.tool_activity_state<>'not_observed' OR n.tool_activity_count IS NOT NULL
                 OR n.subagent_activity_state<>'not_observed' OR n.subagent_activity_count IS NOT NULL
                 OR n.error_activity_state<>'not_observed' OR n.error_activity_count IS NOT NULL
                 OR n.retry_activity_state<>'not_observed' OR n.retry_activity_count IS NOT NULL
                 OR n.token_authority<>'none' OR n.token_state<>'not_observed'
                 OR n.available_execution_count<>0 OR n.total_execution_count<>1
                 OR n.input_token_state<>'not_observed' OR n.input_tokens IS NOT NULL
                 OR n.output_token_state<>'not_observed' OR n.output_tokens IS NOT NULL
                 OR n.total_token_state<>'not_observed' OR n.total_tokens IS NOT NULL
                 OR n.reasoning_token_state<>'not_observed' OR n.reasoning_tokens IS NOT NULL
                 OR n.cache_read_token_state<>'not_observed' OR n.cache_read_tokens IS NOT NULL
                 OR n.cache_creation_token_state<>'not_observed' OR n.cache_creation_tokens IS NOT NULL
                 OR n.new_input_token_state<>'not_observed' OR n.new_input_tokens IS NOT NULL
                 OR n.cache_read_ratio_state<>'not_observed' OR n.cache_read_ratio_basis_points IS NOT NULL
                 OR n.retry_relation_state<>'not_observed' OR n.recovery_relation_state<>'not_observed'
                 OR (SELECT COUNT(*) FROM local_workspace_node_edges edge WHERE edge.node_id=n.node_id)<>
                    CASE WHEN e.expected_relationship IN ('exact','explicit') THEN 1 ELSE 0 END
                  OR e.expected_relationship IN ('exact','explicit') AND NOT EXISTS(
                     SELECT 1 FROM local_workspace_node_edges edge WHERE edge.node_id=n.node_id
                       AND edge.related_node_id=e.expected_parent AND edge.relation_kind='parent'
                       AND edge.relationship_authority=e.expected_relationship AND edge.source_ordinal=e.expected_ordinal)))
            SELECT EXISTS(SELECT 1 FROM invalid);
            """, sessionId);
        command.Parameters.AddWithValue("$skills", System.Text.Json.JsonSerializer.Serialize(skills));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) != 0)
            RejectSkillOwnerGraph();
    }

    private static string SkillReferenceRevision(SkillProjectionCanonicalInvocation invocation) =>
        invocation.CanonicalIdentity + "|" + (invocation.OtelSourceIdentity ?? string.Empty) + "|" +
        (invocation.SdkSourceIdentity ?? string.Empty);

    private static string? PersistedSkillReferenceRevision(PersistedSkillProof proof)
    {
        var otelReferences = proof.References.Where(static reference =>
            reference.SourceIdentity?.StartsWith("otel:", StringComparison.Ordinal) == true).Take(2).ToArray();
        var sdkReferences = proof.References.Where(static reference =>
            reference.SourceIdentity?.StartsWith("sdk:", StringComparison.Ordinal) == true).Take(2).ToArray();
        if (otelReferences.Length > 1 || sdkReferences.Length > 1) return null;
        var otel = otelReferences.SingleOrDefault()?.SourceIdentity;
        var sdk = sdkReferences.SingleOrDefault()?.SourceIdentity;
        if (otel is null && sdk is null) return null;
        if (otel is not null && otelReferences.Single().SourceOrdinal != 0
            || sdkReferences.SingleOrDefault() is { SourceOrdinal: var sdkOrdinal }
               && sdkOrdinal != (otel is null ? 0 : 1)) return null;
        return proof.CanonicalIdentity + "|" + (otel ?? string.Empty) + "|" + (sdk ?? string.Empty);
    }

    private static bool SameSkillReference(PersistedSkillReference left, PersistedSkillReference right) =>
        string.Equals(left.SourceKind, right.SourceKind, StringComparison.Ordinal)
        && left.SourceOrdinal == right.SourceOrdinal
        && string.Equals(left.SourceIdentity, right.SourceIdentity, StringComparison.Ordinal)
        && string.Equals(left.TraceId, right.TraceId, StringComparison.Ordinal)
        && string.Equals(left.SpanId, right.SpanId, StringComparison.Ordinal)
        && string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);

    private static bool InactiveSkillReferenceMatchesCanonical(
        PersistedSkillReference reference,
        string canonicalIdentity,
        SkillProjectionCanonicalInvocation? invocation)
    {
        string? traceId;
        string? spanId;
        if (reference.SourceIdentity?.StartsWith("sdk:", StringComparison.Ordinal) == true)
        {
            if (string.Equals(canonicalIdentity, reference.SourceIdentity, StringComparison.Ordinal))
                return invocation is null;
            traceId = reference.OwnerProducerTraceId;
            spanId = reference.OwnerProducerSpanId;
        }
        else if (reference.SourceIdentity?.StartsWith("otel:", StringComparison.Ordinal) == true)
        {
            traceId = reference.TraceId;
            spanId = reference.SpanId;
        }
        else return false;
        if (traceId is null || spanId is null
            || !string.Equals(canonicalIdentity, "producer:" + traceId + ":" + spanId, StringComparison.Ordinal))
            return false;
        return invocation is null
            || string.Equals(invocation.ProducerTraceId, traceId, StringComparison.Ordinal)
               && string.Equals(invocation.ProducerSpanId, spanId, StringComparison.Ordinal);
    }

    private static bool MetadataMatches(PersistedSkillProof proof, SkillProjectionCanonicalInvocation invocation) =>
        (string.Equals(proof.CurrentValidState, invocation.CurrentValidState, StringComparison.Ordinal)
         || invocation.CurrentValidState == "current" && proof.CurrentValidState == "certification_pending")
        && proof.InventoryState == "unavailable" && proof.InventoryReference is null
        && string.Equals(proof.SourceState, invocation.SkillSource is null ? "not_observed" : "recorded", StringComparison.Ordinal)
        && string.Equals(proof.Source, invocation.SkillSource, StringComparison.Ordinal)
        && string.Equals(proof.TriggerState, invocation.InvocationTrigger is null ? "not_observed" : "recorded", StringComparison.Ordinal)
        && string.Equals(proof.Trigger, invocation.InvocationTrigger, StringComparison.Ordinal)
        && string.Equals(proof.HistoricalState, invocation.HistoricalSnapshotReference is null ? "not_observed" : "recorded", StringComparison.Ordinal)
        && string.Equals(proof.HistoricalReference, invocation.HistoricalSnapshotReference, StringComparison.Ordinal);

    private static bool MetadataMatchesPersistedOwners(PersistedSkillProof proof, string currentValidState)
    {
        var sdkOwners = proof.References.Where(static reference =>
            reference.SourceIdentity?.StartsWith("sdk:", StringComparison.Ordinal) == true).Take(2).ToArray();
        var otelOwners = proof.References.Where(static reference =>
            reference.SourceIdentity?.StartsWith("otel:", StringComparison.Ordinal) == true).Take(2).ToArray();
        if (sdkOwners.Length > 1 || otelOwners.Length > 1) return false;
        var sdk = sdkOwners.SingleOrDefault();
        var otel = otelOwners.SingleOrDefault();
        if (sdk is null && otel is null) return false;
        var source = sdk?.OwnerSource ?? otel?.OwnerSource;
        var trigger = sdk?.OwnerTrigger ?? otel?.OwnerTrigger;
        var historical = sdk?.OwnerHistoricalReference ?? otel?.OwnerHistoricalReference;
        return string.Equals(proof.CurrentValidState, currentValidState, StringComparison.Ordinal)
            && proof.InventoryState == "unavailable" && proof.InventoryReference is null
            && string.Equals(proof.SourceState, source is null ? "not_observed" : "recorded", StringComparison.Ordinal)
            && string.Equals(proof.Source, source, StringComparison.Ordinal)
            && string.Equals(proof.TriggerState, trigger is null ? "not_observed" : "recorded", StringComparison.Ordinal)
            && string.Equals(proof.Trigger, trigger, StringComparison.Ordinal)
            && string.Equals(proof.HistoricalState, historical is null ? "not_observed" : "recorded", StringComparison.Ordinal)
            && string.Equals(proof.HistoricalReference, historical, StringComparison.Ordinal);
    }

    private static string? PersistedSkillValidState(PersistedSkillProof proof) =>
        (proof.SkillState, proof.SkillCount) switch
        {
            ("recorded", 1) => "current",
            ("certification_pending", null) => "certification_pending",
            ("not_observed", null) => "stale",
            _ => null
        };

    private static void RejectSkillOwnerGraph() =>
        throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");

    private sealed record PersistedSkillProof(
        string NodeId,
        string CanonicalIdentity,
        string CurrentValidState,
        string SourceState,
        string? Source,
        string TriggerState,
        string? Trigger,
        string InventoryState,
        string? InventoryReference,
        string HistoricalState,
        string? HistoricalReference,
        string RegistryIdentity,
        string SkillState,
        long? SkillCount,
        List<PersistedSkillReference> References);

    private sealed record PersistedSkillReference(
        string SourceKind,
        long SourceOrdinal,
        string? SourceIdentity,
        string? TraceId,
        string? SpanId,
        string? EventId,
        string? RevisionInput,
        bool OtelOwnerExists,
        bool SdkOwnerExists,
        bool SdkRetentionActive,
        string? OwnerSource = null,
        string? OwnerTrigger = null,
        string? OwnerHistoricalReference = null,
        string? OwnerProducerTraceId = null,
        string? OwnerProducerSpanId = null)
    {
        internal bool LegitimatelyInactive =>
            SourceIdentity?.StartsWith("otel:", StringComparison.Ordinal) == true && OtelOwnerExists
            || SourceIdentity?.StartsWith("sdk:", StringComparison.Ordinal) == true && SdkOwnerExists && !SdkRetentionActive;
    }

    private async Task<LocalWorkspaceExecutionDetail[]> ReadExecutions(SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request, string[] executionIds, SkillProjectionCurrentInvocationProjection? skillProjection, CancellationToken token)
    {
        statementObserver?.Invoke("detail-executions");
        var selection = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? "h.session_id=$session_id"
            : "h.session_id=$session_id AND h.execution_id IN (SELECT CAST(value AS TEXT) FROM json_each($execution_ids))";
        var admittedSkillIds = AdmittedSkillIdentities(skillProjection);
        var skillPredicate = SkillPredicate(admittedSkillIds, "child");
        using var command = Command(c, t, $"""
            WITH canonical_latest AS (
              SELECT execution_id FROM local_workspace_execution_headers WHERE session_id=$session_id
              ORDER BY CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,
                CASE WHEN time_authority='recorded' THEN start_utc_ticks END DESC,source_ordinal,execution_id LIMIT 1)
            SELECT execution_id,session_id,source_kind,source_identity,source_ordinal,lifecycle,status,model,trace_id,
              time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              r.source_surface,
              CASE WHEN (SELECT COUNT(DISTINCT e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND e.source_application_version IS NOT NULL)=1
                THEN (SELECT MIN(e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity) END,
              COALESCE(children.child_count,0),h.execution_id=(SELECT execution_id FROM canonical_latest)
            FROM local_workspace_execution_headers h
            LEFT JOIN (SELECT session_id run_session_id,run_id,source_surface FROM session_runs) r ON r.run_session_id=h.session_id AND r.run_id=h.source_identity
            LEFT JOIN (
              SELECT root.execution_id execution_child_id,COUNT(child.node_id) child_count
              FROM local_workspace_nodes root
              LEFT JOIN local_workspace_nodes child ON child.session_id=root.session_id AND child.execution_id=root.execution_id AND {skillPredicate}
                AND (child.parent_node_id=root.node_id OR child.kind='unknown_relation_group' AND child.parent_node_id IS NULL)
              WHERE root.session_id=$session_id AND root.source_kind='execution_root'
              GROUP BY root.execution_id
            ) children ON children.execution_child_id=h.execution_id
            WHERE {selection}
            ORDER BY CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,
              CASE WHEN time_authority='recorded' THEN start_utc_ticks END DESC,source_ordinal,execution_id LIMIT 257;
            """, request.SessionId);
        command.Parameters.AddWithValue("$execution_ids", System.Text.Json.JsonSerializer.Serialize(executionIds));
        command.Parameters.AddWithValue("$skill_ids", System.Text.Json.JsonSerializer.Serialize(admittedSkillIds));
        var rows = new List<LocalWorkspaceExecutionDetail>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (rows.Count == MaximumExecutions) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),reader.GetString(5),reader.GetString(6),S(reader,7),S(reader,8),reader.GetString(9),L(reader,10),L(reader,11),L(reader,12),Activity(reader,13),Tokens(reader,23),S(reader,43),S(reader,44),reader.GetInt64(45),reader.GetInt64(46)!=0));
        }
        return rows.ToArray();
    }

    private static async Task<LocalWorkspaceNodeDetail[]> ReadV5NodeFacts(
        SqliteConnection connection, SqliteTransaction transaction,
        LocalRepositorySessionDetailRequest request, LocalWorkspaceNodeDetail[] nodes,
        SkillProjectionCurrentInvocationProjection? skillProjection, CancellationToken token)
    {
        if (nodes.Length == 0) return nodes;
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        var ids = System.Text.Json.JsonSerializer.Serialize(nodes.Select(static node => node.NodeId));
        var references = nodes.ToDictionary(static node => node.NodeId,
            static _ => new List<LocalWorkspaceNodeSourceReferenceDetail>(), StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.node_id,r.source_kind,r.source_identity,r.trace_id,r.span_id,r.event_id,r.revision_input,
                  CASE
                    WHEN n.source_kind='execution_root' AND r.source_kind='session_run'
                     AND r.source_identity=n.source_identity AND r.trace_id IS NULL AND r.span_id IS NULL AND r.event_id IS NULL
                     AND EXISTS(SELECT 1 FROM local_workspace_execution_headers h
                       WHERE h.execution_id=n.execution_id AND h.session_id=n.session_id AND h.source_identity=r.source_identity) THEN 1
                    WHEN n.source_kind='session_event' AND r.source_kind='session_event'
                     AND r.source_identity=n.source_identity AND r.event_id=n.event_id
                     AND r.trace_id IS n.trace_id AND r.span_id IS n.span_id
                     AND EXISTS(SELECT 1 FROM session_events e
                       WHERE e.event_id=r.event_id AND e.session_id=n.session_id
                         AND r.revision_input=e.source_adapter||'|'||e.source_event_id||'|'||e.type||'|'||COALESCE(e.occurred_at,'')) THEN 1
                    WHEN n.source_kind='semantic_tool' AND r.source_kind='otel_span'
                     AND r.trace_id=n.trace_id AND r.span_id=n.span_id AND r.event_id IS NOT NULL
                     AND r.source_identity=r.event_id
                     AND instr(r.revision_input,'otel-exact|'||r.trace_id||'/'||r.span_id||'|otel.tool.')=1
                     AND substr(r.revision_input,-length('|otel-exact|normalized-tool-span|v1'))='|otel-exact|normalized-tool-span|v1'
                     AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                       JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id AND h.session_id=n.session_id
                       JOIN session_events e ON e.event_id=r.event_id AND e.session_id=n.session_id AND e.run_id=h.source_identity
                       WHERE receipt.node_id=n.node_id AND receipt.semantic_kind='tool' AND receipt.source_family='otel'
                         AND receipt.carrier_digest=n.source_identity AND receipt.authority_receipt='otel-exact|normalized-tool-span|v1'
                         AND e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY
                         AND e.trace_id=r.trace_id COLLATE BINARY
                         AND e.source_event_id=r.trace_id||'/'||r.span_id COLLATE BINARY) THEN 1
                    WHEN n.source_kind='semantic_tool' AND r.source_kind='session_event'
                     AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                       JOIN session_events e ON e.event_id=r.event_id AND e.session_id=n.session_id
                       JOIN session_runs run ON run.run_id=e.run_id AND run.session_id=e.session_id
                       JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
                       JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id AND h.session_id=n.session_id AND h.source_identity=e.run_id
                       LEFT JOIN session_events parent ON parent.event_id=e.parent_event_id AND parent.session_id=e.session_id AND parent.run_id=e.run_id
                       WHERE receipt.node_id=n.node_id AND receipt.semantic_kind='tool' AND receipt.source_family='session_sdk'
                         AND receipt.carrier_digest=n.source_identity AND e.source_surface='copilot-sdk' COLLATE BINARY
                         AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
                         AND r.source_identity=e.event_id AND r.event_id=e.event_id
                         AND ((e.type='tool.execution_start' AND e.source_event_id IS NOT NULL
                               AND n.source_identity=local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                                 local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,e.source_event_id)))
                           OR (e.type='tool.execution_complete' AND parent.type='tool.execution_start'
                               AND parent.source_adapter=e.source_adapter COLLATE BINARY
                               AND n.source_identity=local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                                 local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,parent.source_event_id))))
                         AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
                         AND instr(r.revision_input,e.source_adapter||'|'||e.source_event_id||'|'||e.type||'|'||e.type||'|1|')=1
                         AND substr(r.revision_input,-length(receipt.authority_receipt)-1)='|'||receipt.authority_receipt) THEN 1
                    WHEN n.source_kind='semantic_subagent' AND r.source_kind='session_event'
                     AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                       JOIN session_events e ON e.event_id=r.event_id AND e.session_id=n.session_id
                       JOIN session_runs run ON run.run_id=e.run_id AND run.session_id=e.session_id
                       JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
                       JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id AND h.session_id=n.session_id AND h.source_identity=e.run_id
                       WHERE receipt.node_id=n.node_id AND receipt.semantic_kind='subagent' AND receipt.source_family='session_sdk'
                         AND receipt.carrier_digest=n.source_identity AND e.source_surface='copilot-sdk' COLLATE BINARY
                         AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
                         AND e.type IN ('subagent.selected','subagent.started','subagent.completed','subagent.failed','subagent.deselected')
                         AND r.source_identity=e.event_id AND r.event_id=e.event_id
                         AND n.source_identity=local_workspace_semantic_digest('session_sdk_subagent',native.native_session_id,run.native_run_id)
                         AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
                         AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=e.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
                         AND instr(r.revision_input,e.source_adapter||'|'||e.source_event_id||'|'||e.type||'|'||e.type||'|1|')=1
                         AND substr(r.revision_input,-length(receipt.authority_receipt)-1)='|'||receipt.authority_receipt) THEN 1
                    WHEN n.source_kind IN ('semantic_tool','semantic_subagent') AND r.source_kind='session_event'
                     AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                       JOIN session_events e ON e.event_id=r.event_id AND e.session_id=n.session_id
                       JOIN session_runs run ON run.run_id=e.run_id AND run.session_id=e.session_id
                       JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='claude-code' COLLATE BINARY
                       JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id AND h.session_id=n.session_id AND h.source_identity=e.run_id
                       WHERE receipt.node_id=n.node_id AND receipt.source_family='claude_hook'
                         AND receipt.carrier_digest=n.source_identity AND e.source_surface='claude-code' COLLATE BINARY
                         AND e.source_adapter='claude-code-hook' COLLATE BINARY AND run.source_surface='claude-code' COLLATE BINARY
                         AND r.source_identity=e.event_id AND r.event_id=e.event_id
                         AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>''
                         AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                         AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'')
                           OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'))
                         AND ((receipt.semantic_kind='tool' AND n.source_kind='semantic_tool'
                               AND e.type IN ('PreToolUse','PostToolUse','PostToolUseFailure'))
                           OR (receipt.semantic_kind='subagent' AND n.source_kind='semantic_subagent'
                               AND e.type IN ('SubagentStart','SubagentStop')
                               AND n.source_identity=local_workspace_semantic_digest('claude_hook_subagent',native.native_session_id,run.native_run_id)))
                         AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='claude-code' COLLATE BINARY)=1
                         AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=e.session_id AND candidate.source_surface='claude-code' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
                         AND instr(r.revision_input,e.source_adapter||'|'||e.source_event_id||'|'||e.type||'|'||e.type||'|1|')=1
                         AND substr(r.revision_input,-length(receipt.authority_receipt)-1)='|'||receipt.authority_receipt) THEN 1
                    WHEN n.source_kind='skill_invocation' AND r.source_kind='skill_claim'
                     AND ((r.source_identity=n.otel_source_identity AND r.trace_id=n.trace_id AND r.span_id=n.span_id)
                       OR (r.source_identity=n.sdk_source_identity AND r.trace_id IS NULL AND r.span_id IS NULL)) THEN 1
                    ELSE 0
                  END authority_validated
                FROM local_workspace_node_source_references r
                JOIN local_workspace_nodes n ON n.node_id=r.node_id
                WHERE r.node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                ORDER BY r.node_id,r.source_ordinal;
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                references[reader.GetString(0)].Add(new(reader.GetString(1), S(reader, 2), S(reader, 3), S(reader, 4), S(reader, 5), reader.GetString(6), reader.GetInt64(7) == 1));
        }
        var tools = new Dictionary<string, LocalWorkspaceToolMetadataDetail>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id,caller_state,caller_node_id,started_state,completed_state,failed_state,exit_state,exit_code,
                  mcp_server_identity_state,mcp_server_identity,mcp_server_name_state,mcp_server_name,mcp_tool_name_state,mcp_tool_name,
                  retry_state,recovery_state,child_activity_state,child_activity_count
                FROM local_workspace_tool_metadata WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                tools.Add(reader.GetString(0), new(reader.GetString(1),S(reader,2),reader.GetString(3),reader.GetString(4),reader.GetString(5),
                    reader.GetString(6),L(reader,7),reader.GetString(8),S(reader,9),reader.GetString(10),S(reader,11),reader.GetString(12),S(reader,13),
                    reader.GetString(14),reader.GetString(15),reader.GetString(16),L(reader,17)));
        }
        var skills = new Dictionary<string, LocalWorkspaceSkillMetadataDetail>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id,current_valid_state,source_state,source,trigger_state,trigger,inventory_reference_state,inventory_reference,
                  historical_snapshot_reference_state,historical_snapshot_reference
                FROM local_workspace_skill_metadata WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                skills.Add(reader.GetString(0), new(reader.GetString(1),reader.GetString(2),S(reader,3),reader.GetString(4),S(reader,5),
                    reader.GetString(6),S(reader,7),reader.GetString(8),S(reader,9)));
        }
        var subagents = new Dictionary<string, LocalWorkspaceSubagentLifecycleDetail>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id,selected_state,started_state,completed_state,failed_state,deselected_state,input_state
                FROM local_workspace_subagent_lifecycle WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                subagents.Add(reader.GetString(0), new(reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6)));
        }
        var returnedChildren = nodes.Where(static node => node.ParentNodeId is not null)
            .GroupBy(static node => node.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.LongCount(), StringComparer.Ordinal);
        var expandedTimelineNodeId = request.Kind == LocalRepositorySessionDetailRequestKind.Timeline
            ? request.ParentNodeId ?? nodes.FirstOrDefault(node => node.SourceKind == "execution_root" && node.ExecutionId == request.ExecutionId)?.NodeId
            : null;
        var timelineCandidateCount = expandedTimelineNodeId is null
            ? 0
            : nodes.LongCount(node => node.NodeId != request.After?.NodeId
                && (node.ParentNodeId == expandedTimelineNodeId
                    || request.ParentNodeId is null && node.ExecutionId == request.ExecutionId
                    && node.SourceKind == "unknown_relation_group" && node.ParentNodeId is null));
        return nodes.Select(node =>
        {
            var isSelectedNode = request.Kind == LocalRepositorySessionDetailRequestKind.Node && node.NodeId == request.NodeId;
            var isExpandedTimelineNode = node.NodeId == expandedTimelineNodeId;
            var collapsed = isSelectedNode
                ? Math.Max(0, node.ChildCount - returnedChildren.GetValueOrDefault(node.NodeId))
                : node.ChildCount;
            var hasMore = isExpandedTimelineNode
                ? timelineCandidateCount > request.Limit
                : isSelectedNode ? collapsed > 0 : node.ChildCount > 0;
            var currentReferences = CurrentSourceReferences(node, references[node.NodeId], skillProjection);
            var toolMetadata = tools.GetValueOrDefault(node.NodeId);
            var subagentLifecycle = subagents.GetValueOrDefault(node.NodeId);
            return node with
            {
                HasMoreChildren = hasMore,
                CollapsedChildren = new("complete", collapsed),
                SourceReferences = currentReferences,
                ToolMetadata = toolMetadata is null ? null : toolMetadata with { SourceReferences = currentReferences },
                SkillMetadata = skills.GetValueOrDefault(node.NodeId),
                SubagentLifecycle = subagentLifecycle is null ? null : subagentLifecycle with { SourceReferences = currentReferences },
                PermissionMetadata = node.SourceKind == "session_event" && node.Kind == "permission"
                    ? new("not_observed", null, "not_observed", null)
                    : null,
            };
        }).ToArray();
    }

    private static IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail> CurrentSourceReferences(
        LocalWorkspaceNodeDetail node,
        IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail> persisted,
        SkillProjectionCurrentInvocationProjection? skillProjection)
    {
        IEnumerable<LocalWorkspaceNodeSourceReferenceDetail> selected = persisted;
        if (node.SourceKind == "skill_invocation" && skillProjection?.State is "current" or "certification_pending")
        {
            var invocation = skillProjection.Invocations.SingleOrDefault(value =>
                string.Equals(value.CanonicalIdentity, node.SourceIdentity, StringComparison.Ordinal));
            if (invocation is not null)
            {
                var current = new List<LocalWorkspaceNodeSourceReferenceDetail>(2);
                var revision = SkillReferenceRevision(invocation);
                if (invocation.OtelSourceIdentity is not null)
                    current.Add(new("skill_claim", invocation.OtelSourceIdentity, invocation.ProducerTraceId,
                        invocation.ProducerSpanId, invocation.OtelCarrierEventId, revision, true));
                if (invocation.SdkSourceIdentity is not null)
                    current.Add(new("skill_claim", invocation.SdkSourceIdentity, null, null,
                        invocation.SdkCarrierEventId, revision, true));
                selected = current;
            }
        }
        return Array.AsReadOnly(selected.OrderBy(ReferenceKey, StringComparer.Ordinal).ToArray());
    }

    private static string ReferenceKey(LocalWorkspaceNodeSourceReferenceDetail value) =>
        string.Join('\u001f', value.SourceKind, value.SourceIdentity ?? string.Empty, value.TraceId ?? string.Empty,
            value.SpanId ?? string.Empty, value.EventId ?? string.Empty);

    private static LocalWorkspaceNodeDetail[] ApplyCurrentSkillMetadata(
        LocalWorkspaceNodeDetail[] nodes, SkillProjectionCurrentInvocationProjection? projection)
    {
        if (projection?.State is not ("current" or "certification_pending")) return nodes;
        var current = projection.Invocations.ToDictionary(static invocation => invocation.CanonicalIdentity, StringComparer.Ordinal);
        return nodes.Select(node => node.SourceKind == "skill_invocation" && current.TryGetValue(node.SourceIdentity, out var invocation)
            ? node with
            {
                SkillMetadata = new(invocation.CurrentValidState,
                    invocation.SkillSource is null ? "not_observed" : "recorded", invocation.SkillSource,
                    invocation.InvocationTrigger is null ? "not_observed" : "recorded", invocation.InvocationTrigger,
                    "unavailable", null,
                    invocation.HistoricalSnapshotReference is null ? "not_observed" : "recorded", invocation.HistoricalSnapshotReference)
            }
            : node).ToArray();
    }

    private async Task<LocalWorkspaceNodeDetail[]> ReadNodes(
        SqliteConnection c,
        SqliteTransaction t,
        LocalRepositorySessionDetailRequest request,
        SkillProjectionCurrentInvocationProjection? skillProjection,
        CancellationToken token)
    {
        statementObserver?.Invoke("detail-nodes");
        var context = request.Kind == LocalRepositorySessionDetailRequestKind.Timeline
            ? await ReadTimelineContext(c, t, request, skillProjection, token)
            : [];
        var predicate = request.Kind switch
        {
            LocalRepositorySessionDetailRequestKind.Summary => "session_id=$session_id AND source_kind='execution_root'",
            LocalRepositorySessionDetailRequestKind.Compare => "session_id=$session_id AND kind IN ('skill','tool','subagent')",
            LocalRepositorySessionDetailRequestKind.Timeline when request.ExecutionId is null => "session_id=$session_id AND source_kind='execution_root'",
            LocalRepositorySessionDetailRequestKind.Timeline when request.ParentNodeId is null => "session_id=$session_id AND execution_id=$execution_id AND (parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE session_id=$session_id AND execution_id=$execution_id AND source_kind='execution_root') OR (kind='unknown_relation_group' AND parent_node_id IS NULL))",
            LocalRepositorySessionDetailRequestKind.Timeline => "session_id=$session_id AND execution_id=$execution_id AND parent_node_id=$parent_node_id",
            _ => """
                session_id=$session_id AND (node_id=$node_id
                OR node_id=(SELECT root.node_id FROM local_workspace_nodes selected
                  JOIN local_workspace_nodes root ON root.session_id=selected.session_id
                    AND root.execution_id=selected.execution_id AND root.source_kind='execution_root'
                  WHERE selected.session_id=$session_id AND selected.node_id=$node_id)
                OR node_id IN (WITH RECURSIVE ancestors(node_id,depth) AS (SELECT parent_node_id,1 FROM local_workspace_nodes WHERE session_id=$session_id AND node_id=$node_id UNION ALL SELECT n.parent_node_id,a.depth+1 FROM local_workspace_nodes n JOIN ancestors a ON n.node_id=a.node_id WHERE a.node_id IS NOT NULL AND a.depth<=4097) SELECT node_id FROM ancestors WHERE node_id IS NOT NULL)
                OR node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id=$session_id AND parent_node_id=$node_id ORDER BY CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,CASE WHEN time_authority='recorded' THEN start_utc_ticks END,source_ordinal,node_id LIMIT 201)
                OR node_id IN (SELECT related_node_id FROM local_workspace_node_edges WHERE node_id=$node_id AND relation_kind='retry' ORDER BY source_ordinal,related_node_id LIMIT 201)
                OR node_id IN (SELECT related_node_id FROM local_workspace_node_edges WHERE node_id=$node_id AND relation_kind='recovery' ORDER BY source_ordinal,related_node_id LIMIT 201))
                """
        };
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && request.After is not null)
        {
            predicate += " AND ((CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END)>$after_group OR ((CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END)=$after_group AND (CASE WHEN time_authority='recorded' THEN start_utc_ticks ELSE 0 END)>$after_ticks) OR ((CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END)=$after_group AND (CASE WHEN time_authority='recorded' THEN start_utc_ticks ELSE 0 END)=$after_ticks AND source_ordinal>$after_ordinal) OR ((CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END)=$after_group AND (CASE WHEN time_authority='recorded' THEN start_utc_ticks ELSE 0 END)=$after_ticks AND source_ordinal=$after_ordinal AND node_id>$after_node_id COLLATE BINARY))";
        }
        var admittedSkillIds = AdmittedSkillIdentities(skillProjection);
        var skillPredicate = SkillPredicate(admittedSkillIds);
        if (request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content)
            predicate = predicate.Replace(
                "WHERE session_id=$session_id AND parent_node_id=$node_id ORDER BY",
                $"WHERE session_id=$session_id AND parent_node_id=$node_id AND {skillPredicate} ORDER BY",
                StringComparison.Ordinal);
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline)
            predicate += " AND " + skillPredicate;
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Compare)
            predicate += " AND " + skillPredicate;
        var limit = request.Kind switch
        {
            LocalRepositorySessionDetailRequestKind.Summary => 257,
            LocalRepositorySessionDetailRequestKind.Node => 4097,
            LocalRepositorySessionDetailRequestKind.Compare => 4097,
            _ => request.Limit + 1
        };
        var orderPrefix = request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && request.ExecutionId is null
            ? ""
            : "execution_id,";
        using var command = Command(c, t, $"""
            SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              trace_id,span_id,event_id,COALESCE(children.child_count,0)
            FROM local_workspace_nodes
            LEFT JOIN (SELECT parent_node_id child_parent_id,COUNT(*) child_count FROM local_workspace_nodes WHERE session_id=$session_id AND parent_node_id IS NOT NULL AND {skillPredicate} GROUP BY parent_node_id) children
              ON children.child_parent_id=local_workspace_nodes.node_id
            WHERE {predicate} ORDER BY {orderPrefix}
              CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,
              CASE WHEN time_authority='recorded' THEN start_utc_ticks END,source_ordinal,node_id LIMIT {limit};
            """, request.SessionId);
        command.Parameters.AddWithValue("$execution_id", (object?)request.ExecutionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_node_id", (object?)request.ParentNodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$node_id", (object?)request.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$after_group", request.After?.TimeGroup ?? 0);
        command.Parameters.AddWithValue("$after_ticks", request.After?.UtcTicks ?? 0);
        command.Parameters.AddWithValue("$after_ordinal", request.After is null ? 0L : checked((long)request.After.SourceOrdinal));
        command.Parameters.AddWithValue("$after_node_id", (object?)request.After?.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$skill_ids", System.Text.Json.JsonSerializer.Serialize(admittedSkillIds));
        var rows = new List<LocalWorkspaceNodeDetail>(context);
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (request.Kind == LocalRepositorySessionDetailRequestKind.Summary && rows.Count == MaximumExecutions
                || request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Compare && rows.Count == MaximumNodes)
                throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49),reader.GetInt64(50)));
        }
        return rows.ToArray();
    }

    private static async Task<LocalWorkspaceNodeDetail[]> ReadTimelineContext(
        SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request,
        SkillProjectionCurrentInvocationProjection? skillProjection, CancellationToken token)
    {
        var predicates = new List<string>();
        if (request.ExecutionId is not null)
            predicates.Add("source_kind='execution_root' AND execution_id=$execution_id");
        if (request.ParentNodeId is not null)
            predicates.Add("node_id=$parent_node_id AND execution_id=$execution_id");
        if (request.After is not null)
        {
            var membership = request.ExecutionId is null
                ? "source_kind='execution_root'"
                : request.ParentNodeId is null
                    ? "(parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE session_id=$session_id AND execution_id=$execution_id AND source_kind='execution_root') OR kind='unknown_relation_group' AND parent_node_id IS NULL)"
                    : "parent_node_id=$parent_node_id";
            predicates.Add($"node_id=$after_node_id AND {membership} AND (CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END)=$after_group AND (CASE WHEN time_authority='recorded' THEN start_utc_ticks ELSE 0 END)=$after_ticks AND source_ordinal=$after_ordinal");
        }
        if (predicates.Count == 0) return [];
        var admittedSkillIds = AdmittedSkillIdentities(skillProjection);
        var skillPredicate = SkillPredicate(admittedSkillIds);
        using var command = Command(c, t, $"""
            SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              trace_id,span_id,event_id,COALESCE(children.child_count,0)
            FROM local_workspace_nodes
            LEFT JOIN (SELECT parent_node_id child_parent_id,COUNT(*) child_count FROM local_workspace_nodes WHERE session_id=$session_id AND parent_node_id IS NOT NULL AND {skillPredicate} GROUP BY parent_node_id) children
              ON children.child_parent_id=local_workspace_nodes.node_id
            WHERE session_id=$session_id AND ({string.Join(" OR ", predicates)})
            ORDER BY node_id LIMIT 4;
            """, request.SessionId);
        command.Parameters.AddWithValue("$execution_id", (object?)request.ExecutionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_node_id", (object?)request.ParentNodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$after_group", request.After?.TimeGroup ?? 0);
        command.Parameters.AddWithValue("$after_ticks", request.After?.UtcTicks ?? 0);
        command.Parameters.AddWithValue("$after_ordinal", request.After is null ? 0L : checked((long)request.After.SourceOrdinal));
        command.Parameters.AddWithValue("$after_node_id", (object?)request.After?.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$skill_ids", System.Text.Json.JsonSerializer.Serialize(admittedSkillIds));
        var rows = new List<LocalWorkspaceNodeDetail>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49),reader.GetInt64(50)));
        if (request.ExecutionId is not null && rows.Count(static row => row.SourceKind == "execution_root") != 1
            && await ExecutionExists(c, t, request, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        return rows.DistinctBy(static row => row.NodeId, StringComparer.Ordinal).ToArray();
    }

    private static string[] AdmittedSkillIdentities(SkillProjectionCurrentInvocationProjection? projection) =>
        projection?.State is "current" or "certification_pending"
            ? projection.Invocations.Select(static invocation => invocation.CanonicalIdentity)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [];

    private static string SkillPredicate(IReadOnlyCollection<string> admittedSkillIds, string? alias = null)
    {
        var prefix = alias is null ? string.Empty : alias + ".";
        return admittedSkillIds.Count == 0
            ? prefix + "source_kind<>'skill_invocation'"
            : $"({prefix}source_kind<>'skill_invocation' OR {prefix}source_identity IN (SELECT CAST(value AS TEXT) FROM json_each($skill_ids)))";
    }

    private static async Task<bool> ExecutionExists(SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request, CancellationToken token)
    {
        using var command = Command(c, t, "SELECT 1 FROM local_workspace_execution_headers WHERE session_id=$session_id AND execution_id=$execution_id LIMIT 1;", request.SessionId);
        command.Parameters.AddWithValue("$execution_id", request.ExecutionId!);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private async Task<LocalWorkspaceNodeEdgeDetail[]> ReadEdges(SqliteConnection c, SqliteTransaction t, string[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        statementObserver?.Invoke("detail-edges");
        using var command = c.CreateCommand(); command.Transaction=t;
        command.CommandText="""
            SELECT node_id,related_node_id,relation_kind,relationship_authority,source_ordinal FROM (
              SELECT node_id,related_node_id,relation_kind,relationship_authority,source_ordinal,
                ROW_NUMBER() OVER (PARTITION BY node_id,relation_kind ORDER BY source_ordinal,related_node_id) ordinal
              FROM local_workspace_node_edges
              WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) AND relation_kind IN ('retry','recovery')
            ) WHERE ordinal<=200 ORDER BY node_id,relation_kind,source_ordinal,related_node_id;
            """;
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        var rows=new List<LocalWorkspaceNodeEdgeDetail>(); using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token)) rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4)));
        return rows.ToArray();
    }

    private async Task<LocalWorkspaceContentAvailability[]> ReadContent(SqliteConnection c, SqliteTransaction t, string[] ids, DateTimeOffset acceptedAt, CancellationToken token)
    {
        if(ids.Length==0)return [];
        statementObserver?.Invoke("detail-content"); using var command=c.CreateCommand();command.Transaction=t;
        command.CommandText=$"SELECT c.node_id,c.part,{LocalWorkspaceContentAuthority.EffectiveAvailabilitySql},c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id LEFT JOIN session_event_content s ON s.event_id=e.event_id LEFT JOIN retention_items i ON i.item_id=c.retention_item_id LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id WHERE c.node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY c.node_id,c.part;";
        command.Parameters.AddWithValue("$now", Canonical(acceptedAt));
        command.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(ids));var rows=new List<LocalWorkspaceContentAvailability>();using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))rows.Add(Content(reader));return rows.ToArray();
    }

    private async Task<LocalWorkspaceContentAvailability[]> ReadSummaryContent(SqliteConnection c, SqliteTransaction t, string sessionId, DateTimeOffset acceptedAt, CancellationToken token)
    {
        statementObserver?.Invoke("detail-summary-content");
        using var command = Command(c, t, $$"""
            SELECT c.node_id,c.part,{{LocalWorkspaceContentAuthority.EffectiveAvailabilitySql}},c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token
            FROM local_workspace_node_content_refs c
            JOIN local_workspace_nodes n ON n.node_id=c.node_id
            JOIN local_workspace_sessions session ON session.session_id=n.session_id
            JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id
            LEFT JOIN session_event_content s ON s.event_id=e.event_id
            LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
            LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id
            WHERE n.session_id=$session_id AND c.part='instruction' AND c.source_item_id=session.label_source_identity
            ORDER BY c.node_id LIMIT 2;
            """, sessionId);
        command.Parameters.AddWithValue("$now", Canonical(acceptedAt));
        var rows = new List<LocalWorkspaceContentAvailability>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(Content(reader));
        if (rows.Count > 1) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        return rows.ToArray();
    }

    private static LocalWorkspaceContentAvailability Content(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), S(reader, 7), L(reader, 8), S(reader, 9), S(reader, 10),
        S(reader, 11), S(reader, 12), L(reader, 13), reader.IsDBNull(14) ? null : (byte[])reader.GetValue(14),
        reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15));

    private static async Task<Metadata> ReadMetadata(SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(c);
        var nativeIds = new List<string>();
        using (var command = Command(c,t,"SELECT DISTINCT native_session_id FROM session_native_ids WHERE session_id=$session_id ORDER BY native_session_id COLLATE BINARY LIMIT 4097;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) nativeIds.Add(reader.GetString(0));
        var versions = new List<string>();
        using (var command = Command(c,t,"SELECT value FROM (SELECT source_application_version value FROM session_events WHERE session_id=$session_id UNION SELECT adapter_version FROM session_events WHERE session_id=$session_id UNION SELECT normalization_version FROM session_events WHERE session_id=$session_id) WHERE value IS NOT NULL AND trim(value)<>'' ORDER BY value COLLATE BINARY;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) versions.Add(reader.GetString(0));
        var labelCarriers = TableExists(c, t, "retention_items")
            ? """
              SELECT event.event_id,event.type,event.occurred_at,content.captured_at,
                     content.expires_at source_expires_at,
                     projected.instruction_count
              FROM session_events event
              JOIN session_event_content content ON content.event_id=event.event_id
              JOIN local_workspace_sessions projected ON projected.session_id=event.session_id
                AND projected.label_state='recorded' AND projected.label_source_identity=event.event_id
              JOIN retention_items item ON item.store_kind='session_event_content'
                AND item.source_item_id=event.event_id AND item.expires_at=content.expires_at COLLATE BINARY
              CROSS JOIN projection_clock clock
              WHERE event.session_id=$session_id
                AND event.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                AND event.content_state='available'
                AND item.state IN ('expiring','retained_by_policy')
                AND item.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                AND item.captured_at=content.captured_at COLLATE BINARY AND item.expires_at=content.expires_at COLLATE BINARY
                AND local_workspace_retention_receipt_matches(item.store_instance_id,event.event_id,content.content_kind,
                  content.captured_at,content.expires_at,event.session_id,event.run_id,event.source_adapter,event.source_event_id,
                  content.retention_owner_token,item.ownership_receipt)=1
                AND item.read_denied_at IS NULL AND item.deleted_at IS NULL AND item.error_code IS NULL
                AND (item.state='retained_by_policy' OR item.expires_at COLLATE BINARY>clock.refreshed_at COLLATE BINARY)
              """
            : """
              SELECT event.event_id,event.type,event.occurred_at,content.captured_at,
                     content.expires_at source_expires_at,
                     projected.instruction_count
              FROM session_events event
              JOIN session_event_content content ON content.event_id=event.event_id
              JOIN local_workspace_sessions projected ON projected.session_id=event.session_id
                AND projected.label_state='recorded' AND projected.label_source_identity=event.event_id
              CROSS JOIN projection_clock clock
              WHERE event.session_id=$session_id
                AND event.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                AND event.content_state='available'
                AND content.expires_at COLLATE BINARY>clock.refreshed_at COLLATE BINARY
              """;
        string? source = null; long? additional = null;
        using (var command = Command(c,t,$"""
            WITH projection_clock AS (
              SELECT refreshed_at FROM local_workspace_projection_state
              WHERE projector_key='local-workspace-projection-v1'),
            carriers AS ({labelCarriers}),
            exact_label AS (
              SELECT carrier.event_id,carrier.instruction_count
              FROM carriers carrier
              JOIN local_workspace_sessions projected ON projected.session_id=$session_id
              WHERE projected.label_state='recorded'
                AND projected.label_source_identity=carrier.event_id COLLATE BINARY
                AND projected.label_expires_at=carrier.source_expires_at COLLATE BINARY
                AND projected.instruction_count=carrier.instruction_count
                AND projected.label_owner_revision=local_workspace_semantic_digest('session_label_owner',
                  local_workspace_semantic_digest('session_label_source',carrier.event_id,carrier.type),
                  local_workspace_semantic_digest('session_label_value',projected.label_text,
                    local_workspace_semantic_digest('session_label_time',carrier.occurred_at,carrier.captured_at)||
                    local_workspace_semantic_digest('session_label_expiry',carrier.source_expires_at,CAST(carrier.instruction_count AS TEXT)))) COLLATE BINARY)
            SELECT event_id,instruction_count-1 FROM exact_label;
            """,sessionId))
        using (var reader = await command.ExecuteReaderAsync(token))
            if(await reader.ReadAsync(token)){source=reader.GetString(0);additional=reader.GetInt64(1);}
        return new(Array.AsReadOnly(nativeIds.ToArray()),Array.AsReadOnly(versions.ToArray()),source,additional);
    }

    private static async Task<ComparisonVersions> ReadComparisonVersions(
        SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        var sourceVersions = await Read("source_application_version");
        var adapterVersions = await Read("adapter_version");
        return new(sourceVersions, adapterVersions);

        async Task<IReadOnlyList<string>> Read(string column)
        {
            using var command = Command(c, t, $"""
                SELECT DISTINCT {column} FROM session_events
                WHERE session_id=$session_id AND {column} IS NOT NULL AND trim({column})<>''
                ORDER BY {column} COLLATE BINARY LIMIT 64;
                """, sessionId);
            var values = new List<string>();
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var value = reader.GetString(0);
                if (!LocalComparisonVersionToken.IsValid(value))
                    throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
                values.Add(value);
            }
            if (values.Count > 63)
                throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            return Array.AsReadOnly(values.ToArray());
        }
    }

    private static async Task<string> ReadCanonicalRevisionInput(
        SqliteConnection c, SqliteTransaction t, string sessionId, DateTimeOffset acceptedAt,
        SkillProjectionCurrentInvocationProjection? skillProjection, string registryIdentity, CancellationToken token)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-revision-input\0v2\0typed-sqlite-value\0"));
        var statements = new List<string>
        {
            "SELECT * FROM sessions WHERE session_id=$session_id",
            "SELECT * FROM session_native_ids WHERE session_id=$session_id ORDER BY source_surface,native_session_id",
            "SELECT * FROM session_runs WHERE session_id=$session_id ORDER BY run_id",
            "SELECT * FROM session_events WHERE session_id=$session_id ORDER BY event_id",
            "SELECT m.* FROM monitor_spans m WHERE EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=$session_id AND e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY) ORDER BY m.raw_record_id,m.span_ordinal",
            "SELECT r.id,r.source,r.trace_id,r.received_at,r.schema_version,r.retention_owner_token FROM raw_records r WHERE EXISTS(SELECT 1 FROM monitor_spans m JOIN session_events e ON e.session_id=$session_id AND e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY WHERE m.raw_record_id=r.id) ORDER BY r.id",
            "SELECT f.* FROM local_workspace_span_facts f JOIN monitor_spans m ON m.raw_record_id=f.raw_record_id AND m.span_ordinal=f.span_ordinal WHERE EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=$session_id AND e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY) ORDER BY f.raw_record_id,f.span_ordinal",
            "SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,c.retention_owner_token FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE e.session_id=$session_id ORDER BY c.event_id",
            "SELECT session_id,sort_group,sort_epoch_ms,label_state,label_text,label_source_identity,label_expires_at,label_owner_revision,instruction_count,status,completeness,source_state,model_state,timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed FROM local_workspace_sessions WHERE session_id=$session_id",
            "SELECT * FROM local_workspace_execution_headers WHERE session_id=$session_id ORDER BY execution_id LIMIT 257",
            "SELECT * FROM local_workspace_nodes WHERE session_id=$session_id ORDER BY node_id LIMIT 4097",
            "SELECT e.* FROM local_workspace_node_edges e JOIN local_workspace_nodes n ON n.node_id=e.node_id WHERE n.session_id=$session_id ORDER BY e.node_id,e.related_node_id,e.relation_kind LIMIT 4097",
            "SELECT c.* FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id ORDER BY c.node_id,c.part",
            "SELECT r.* FROM local_workspace_semantic_receipts r JOIN local_workspace_nodes n ON n.node_id=r.node_id WHERE n.session_id=$session_id ORDER BY r.node_id",
            "SELECT r.* FROM local_workspace_node_source_references r JOIN local_workspace_nodes n ON n.node_id=r.node_id WHERE n.session_id=$session_id ORDER BY r.node_id,r.source_ordinal",
            "SELECT m.* FROM local_workspace_tool_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT m.* FROM local_workspace_skill_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT m.* FROM local_workspace_subagent_lifecycle m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT x.* FROM local_workspace_content_tombstones x JOIN session_events e ON e.event_id=x.source_item_id WHERE e.session_id=$session_id ORDER BY x.store_kind,x.source_item_id,x.part",
        };
        if (TableExists(c, t, "skill_projection_invocations"))
        {
            statements.Add("SELECT i.* FROM skill_projection_invocations i WHERE i.session_id=$session_id ORDER BY i.generation_id,i.invocation_id");
            statements.Add("SELECT c.* FROM skill_projection_sdk_claims c WHERE c.session_id=$session_id ORDER BY c.claim_id");
            statements.Add("SELECT h.* FROM skill_projection_trace_heads h WHERE EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=$session_id AND r.trace_id=h.trace_id) ORDER BY h.trace_id");
        }
        if (TableExists(c, t, "skill_invocation_snapshots"))
        {
            statements.Add("SELECT s.* FROM skill_invocation_snapshots s WHERE s.session_id=$session_id ORDER BY s.snapshot_id");
            statements.Add("SELECT r.* FROM skill_invocation_snapshot_receipts r JOIN skill_invocation_snapshots s ON s.snapshot_id=r.snapshot_id WHERE s.session_id=$session_id ORDER BY r.snapshot_id");
        }
        statements.AddRange(
        [
            "SELECT i.store_instance_id,i.store_kind,i.source_item_id,i.receipt_version,i.ownership_receipt,i.captured_at,i.expires_at,i.state,i.revision,t.receipt_at,t.deleted_at FROM retention_items i LEFT JOIN retention_tombstones t ON t.item_id=i.item_id WHERE EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id AND c.retention_item_id=i.item_id) ORDER BY i.item_id"
        ]);
        foreach(var sql in statements)
        {
            using var command=Command(c,t,sql,sessionId);using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token)) for(var i=0;i<reader.FieldCount;i++) AppendRevisionValue(hash, reader.GetValue(i));
            hash.AppendData([0xff]);
        }
        using (var overflow = Command(c, t, "SELECT node_overflow FROM local_workspace_sessions WHERE session_id=$session_id", sessionId))
            if (Convert.ToInt64(await overflow.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) != 0)
                AppendRevisionValue(hash, "node_overflow");
        AppendRevisionValue(hash, skillProjection?.State ?? "not_observed");
        AppendRevisionValue(hash, registryIdentity);
        foreach (var invocation in skillProjection?.Invocations.OrderBy(static value => value.CanonicalIdentity, StringComparer.Ordinal)
            ?? Enumerable.Empty<SkillProjectionCanonicalInvocation>())
        {
            AppendRevisionValue(hash, invocation.CanonicalIdentity);
            AppendRevisionValue(hash, invocation.SessionId);
            AppendRevisionValue(hash, (object?)invocation.ProducerTraceId ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.ProducerSpanId ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.OtelSourceIdentity ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.OtelSkillName ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkSourceIdentity ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkSkillName ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkExpiresAt ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.ExecutionSourceKind ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.ExecutionSourceIdentity ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.OtelCarrierEventId ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkCarrierEventId ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkSourceParentEventId ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkSourceAdapter ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SkillSource ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.InvocationTrigger ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.HistoricalSnapshotReference ?? DBNull.Value);
            AppendRevisionValue(hash, invocation.CurrentValidState);
            AppendRevisionValue(hash, (object?)invocation.OtelOwnerRevision ?? DBNull.Value);
            AppendRevisionValue(hash, (object?)invocation.SdkOwnerRevision ?? DBNull.Value);
        }
        using (var command = Command(c, t, $$"""
            SELECT c.node_id,c.part,{{LocalWorkspaceContentAuthority.EffectiveAvailabilitySql}}
            FROM local_workspace_node_content_refs c
            JOIN local_workspace_nodes n ON n.node_id=c.node_id
            JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id
            LEFT JOIN session_event_content s ON s.event_id=e.event_id
            LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
            LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id
            WHERE n.session_id=$session_id ORDER BY c.node_id,c.part;
            """, sessionId))
        {
            command.Parameters.AddWithValue("$now", Canonical(acceptedAt));
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                AppendRevisionValue(hash, reader.GetString(0));
                AppendRevisionValue(hash, reader.GetString(1));
                AppendRevisionValue(hash, reader.GetString(2));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$table COLLATE BINARY);";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    internal static string ComputeTypedRevisionValueDigestForTest(object? value)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-revision-input\0v2\0typed-sqlite-value\0"));
        AppendRevisionValue(hash, value ?? DBNull.Value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendRevisionValue(System.Security.Cryptography.IncrementalHash hash, object value)
    {
        byte tag;
        byte[] data;
        switch (value)
        {
            case DBNull:
                tag = 0;
                data = [];
                break;
            case string text:
                tag = 1;
                data = System.Text.Encoding.UTF8.GetBytes(text);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                tag = 2;
                data = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(data, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                tag = 3;
                data = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(data,
                    BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case byte[] bytes:
                tag = 4;
                data = bytes;
                break;
            default:
                throw new InvalidOperationException("local_workspace_revision_value_type_unsupported");
        }
        hash.AppendData([tag]);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }

    internal sealed class PinnedRegistryAuthority :
        ISkillRegistryGenerationAuthority,
        ISkillRegistryGenerationCapture,
        IDisposable
    {
        private readonly ISkillRegistryGenerationAuthority inner;
        private readonly ISkillRegistryGenerationCapture capture;
        private readonly ISkillRegistryGenerationLease lease;

        private PinnedRegistryAuthority(
            ISkillRegistryGenerationAuthority inner,
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease,
            string canonicalIdentity) =>
            (this.inner, this.capture, this.lease, CanonicalIdentity) = (inner, capture, lease, canonicalIdentity);

        internal string CanonicalIdentity { get; }

        internal static PinnedRegistryAuthority? TryCreate(ISkillRegistryGenerationAuthority authority)
        {
            var capture = authority.CaptureGeneration();
            if (capture is null || !authority.TryAcquireGenerationReadLease(capture, out var lease))
                return null;
            var retained = false;
            try
            {
                if (!authority.VerifyGenerationIdentity(capture, lease))
                    return null;
                var pinned = new PinnedRegistryAuthority(
                    authority, capture, lease, authority.GetCanonicalGenerationIdentity(capture, lease));
                retained = true;
                return pinned;
            }
            finally
            {
                if (!retained)
                    lease.Dispose();
            }
        }

        public ISkillRegistryGenerationCapture CaptureGeneration() => this;

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture candidate,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? generationLease)
        {
            if (!ReferenceEquals(candidate, this))
            {
                generationLease = null;
                return false;
            }
            generationLease = new BorrowedLease(this);
            return true;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture candidate,
            ISkillRegistryGenerationLease generationLease) =>
            ReferenceEquals(candidate, this)
            && generationLease is BorrowedLease borrowed
            && ReferenceEquals(borrowed.Owner, this);

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture candidate,
            ISkillRegistryGenerationLease generationLease)
        {
            if (!VerifyGenerationIdentity(candidate, generationLease))
                throw new InvalidOperationException("skill_registry_generation_not_current");
            return CanonicalIdentity;
        }

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease generationLease, SkillRegistryProducerTuple tuple) =>
            generationLease is BorrowedLease borrowed
            && ReferenceEquals(borrowed.Owner, this)
            && inner.IsProducerTupleAccepted(lease, tuple);

        public void Dispose() => lease.Dispose();

        private sealed class BorrowedLease(PinnedRegistryAuthority owner) : ISkillRegistryGenerationLease
        {
            internal PinnedRegistryAuthority Owner { get; } = owner;
            public void Dispose() { }
        }
    }

    private static string Canonical(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction t,string sql,string sessionId){var command=c.CreateCommand();command.Transaction=t;command.CommandText=sql;command.Parameters.AddWithValue("$session_id",sessionId);return command;}
    private static string? S(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static long? L(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static LocalWorkspaceActivityFacts Activity(SqliteDataReader r,int i)=>new(Fact(r,i),Fact(r,i+2),Fact(r,i+4),Fact(r,i+6),Fact(r,i+8));
    private static LocalWorkspaceFact<long> Fact(SqliteDataReader r,int i)=>new(r.GetString(i),L(r,i+1));
    private static LocalWorkspaceTokenFacts Tokens(SqliteDataReader r,int i)=>new(r.GetString(i),r.GetString(i+1),r.GetInt64(i+2),r.GetInt64(i+3),Fact(r,i+4),Fact(r,i+6),Fact(r,i+8),Fact(r,i+10),Fact(r,i+12),Fact(r,i+14),Fact(r,i+16),Fact(r,i+18));
    private sealed record Metadata(IReadOnlyList<string> NativeSessionIds,IReadOnlyList<string> Versions,string? InstructionSourceIdentity,long? InstructionAdditionalCount);
    private sealed record ComparisonVersions(IReadOnlyList<string> SourceApplicationVersions,IReadOnlyList<string> AdapterVersions);
}

internal sealed class LocalWorkspaceSessionDetailException(string error) : Exception(error)
{
    internal string Error { get; } = error;
}

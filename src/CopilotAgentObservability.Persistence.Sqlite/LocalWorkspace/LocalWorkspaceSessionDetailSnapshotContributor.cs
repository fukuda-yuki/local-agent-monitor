using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionDetailSnapshotContributor : ILocalWorkspaceSessionDetailSnapshotContributor
{
    internal const string BoundsSql = """
        SELECT
          EXISTS(SELECT 1 FROM local_workspace_execution_headers WHERE session_id=$session_id LIMIT 1 OFFSET 256),
          EXISTS(SELECT 1 FROM local_workspace_nodes WHERE session_id=$session_id LIMIT 1 OFFSET 4096)
            OR COALESCE((SELECT node_overflow FROM local_workspace_sessions WHERE session_id=$session_id),0);
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

    public ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        CancellationToken cancellationToken) =>
        transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadAsync(connection, sqliteTransaction, request, token), cancellationToken);

    private async ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, LocalRepositorySessionDetailRequest request, CancellationToken token)
    {
        var sessionId = request.SessionId;
        var acceptedAt = timeProvider.GetUtcNow();
        var currentSkills = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, [sessionId], acceptedAt, registryAuthority);
        currentSkills.TryGetValue(sessionId, out var skillProjection);
        var materializableSkillNodeIds = await ReadMaterializableCurrentSkillNodeIds(
            connection, transaction, sessionId, skillProjection, token);
        await ValidateBounds(connection, transaction, sessionId, materializableSkillNodeIds, token);
        var syntheticSkillTarget = IsCurrentSkillNode(request.NodeId, skillProjection);
        if (request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content
            && !syntheticSkillTarget)
            await ValidateNodeAncestry(connection, transaction, request, token);
        var projectedNodes = NormalizeCurrentSkillNodes(
            await ReadNodes(connection, transaction, request, skillProjection, token), skillProjection);
        var persistedNodeIds = await ReadPersistedCurrentSkillNodeIds(connection, transaction, sessionId, skillProjection, token);
        projectedNodes = await AddMissingCurrentSkillNodes(
            connection, transaction, request, projectedNodes, skillProjection, materializableSkillNodeIds, token);
        var synthesizedSkillNodes = projectedNodes.Where(node => node.SourceKind == "skill_invocation" && !persistedNodeIds.Contains(node.NodeId)).ToArray();
        var admittedSkillIdentities = skillProjection?.State == "current"
            ? skillProjection.Invocations.Select(static invocation => invocation.CanonicalIdentity).ToHashSet(StringComparer.Ordinal)
            : [];
        var excludedSkillNodes = projectedNodes.Where(node => node.SourceKind == "skill_invocation"
            && skillProjection?.State != "certification_pending"
            && !admittedSkillIdentities.Contains(node.SourceIdentity)).ToArray();
        var nodes = projectedNodes.Except(excludedSkillNodes).ToArray();
        var executionIds = nodes.Select(static node => node.ExecutionId).Distinct(StringComparer.Ordinal).ToArray();
        var executions = ApplyCurrentSkillActivity(
            await ReadExecutions(connection, transaction, request, executionIds, token), skillProjection, synthesizedSkillNodes);
        nodes = ApplyCurrentSkillActivity(nodes, executions, excludedSkillNodes, synthesizedSkillNodes);
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
        var metadata = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? await ReadMetadata(connection, transaction, sessionId, token)
            : new Metadata([], [], null, null);
        var content = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? await ReadSummaryContent(connection, transaction, sessionId, acceptedAt, token)
            : await ReadContent(connection, transaction, nodeIds, acceptedAt, token);
        var registryIdentity = ReadRegistryIdentity();
        var revision = await ReadCanonicalRevisionInput(connection, transaction, sessionId, acceptedAt, skillProjection, registryIdentity, token);
        return new(Array.AsReadOnly(executions), Array.AsReadOnly(nodes), Array.AsReadOnly(edges), Array.AsReadOnly(content),
            metadata.NativeSessionIds, metadata.Versions, metadata.InstructionSourceIdentity, metadata.InstructionAdditionalCount, revision, registryIdentity);
    }

    private static bool IsCurrentSkillNode(string? nodeId, SkillProjectionCurrentInvocationProjection? projection) =>
        nodeId is not null && projection?.State == "current" && projection.Invocations.Any(invocation =>
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
        var candidates = projection?.State == "current"
            ? projection.Invocations.Where(static invocation => invocation.ExecutionSourceKind is not null && invocation.ExecutionSourceIdentity is not null)
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
        CancellationToken token)
    {
        if (projection?.State != "current") return nodes;
        var existing = nodes.Select(static node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var additions = new List<LocalWorkspaceNodeDetail>();
        var candidates = projection.Invocations
            .Where(invocation => invocation.ExecutionSourceKind is not null && invocation.ExecutionSourceIdentity is not null)
            .Where(invocation => materializableSkillNodeIds.Contains(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", invocation.CanonicalIdentity)))
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
        if (projection?.State != "current") return nodes;
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
        var current = projection?.State == "current";
        return executions.Select(execution =>
        {
            var count = projection is null
                ? 0
                : projection.Invocations.LongCount(invocation =>
                    string.Equals(invocation.ExecutionSourceKind, execution.SourceKind, StringComparison.Ordinal)
                    && string.Equals(invocation.ExecutionSourceIdentity, execution.SourceIdentity, StringComparison.Ordinal));
            var skill = current && count > 0
                ? new LocalWorkspaceFact<long>("recorded", count)
                : projection?.State == "certification_pending" && count > 0
                    ? new LocalWorkspaceFact<long>("certification_pending", null)
                : projection is null
                    ? new LocalWorkspaceFact<long>("not_observed", null)
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
        LocalWorkspaceNodeDetail[] excluded,
        LocalWorkspaceNodeDetail[] synthesized)
    {
        var excludedByParent = excluded.Where(static node => node.ParentNodeId is not null)
            .GroupBy(static node => node.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.LongCount(), StringComparer.Ordinal);
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
            var childCount = excludedByParent.TryGetValue(node.NodeId, out var removed)
                ? Math.Max(0, node.ChildCount - removed)
                : node.ChildCount;
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
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            executionOverflow = reader.GetInt64(0);
            nodeOverflow = reader.GetInt64(1);
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
        if (executionOverflow != 0 || nodeOverflow != 0)
            throw new LocalWorkspaceSessionDetailException("workspace_too_large");
    }

    private string ReadRegistryIdentity()
    {
        if (registryAuthority is null) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var capture = registryAuthority.CaptureGeneration() ?? throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (!registryAuthority.TryAcquireGenerationReadLease(capture, out var lease)) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        using (lease)
        {
            if (!registryAuthority.VerifyGenerationIdentity(capture, lease)) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            return registryAuthority.GetCanonicalGenerationIdentity(capture, lease);
        }
    }

    private async Task<LocalWorkspaceExecutionDetail[]> ReadExecutions(SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request, string[] executionIds, CancellationToken token)
    {
        statementObserver?.Invoke("detail-executions");
        var selection = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? "h.session_id=$session_id"
            : "h.session_id=$session_id AND h.execution_id IN (SELECT CAST(value AS TEXT) FROM json_each($execution_ids))";
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
              LEFT JOIN local_workspace_nodes child ON child.session_id=root.session_id AND child.execution_id=root.execution_id
                AND (child.parent_node_id=root.node_id OR child.kind='unknown_relation_group' AND child.parent_node_id IS NULL)
              WHERE root.session_id=$session_id AND root.source_kind='execution_root'
              GROUP BY root.execution_id
            ) children ON children.execution_child_id=h.execution_id
            WHERE {selection}
            ORDER BY CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,
              CASE WHEN time_authority='recorded' THEN start_utc_ticks END DESC,source_ordinal,execution_id LIMIT 257;
            """, request.SessionId);
        command.Parameters.AddWithValue("$execution_ids", System.Text.Json.JsonSerializer.Serialize(executionIds));
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
                         AND e.source_adapter='otel-exact' COLLATE BINARY AND e.trace_id=r.trace_id COLLATE BINARY
                         AND e.source_event_id=r.trace_id||'/'||r.span_id COLLATE BINARY) THEN 1
                    WHEN n.source_kind='semantic_tool' AND r.source_kind='session_event'
                     AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                       JOIN session_events e ON e.event_id=r.event_id AND e.session_id=n.session_id
                       JOIN session_runs run ON run.run_id=e.run_id AND run.session_id=e.session_id
                       JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id AND h.session_id=n.session_id AND h.source_identity=e.run_id
                       LEFT JOIN session_events parent ON parent.event_id=e.parent_event_id AND parent.session_id=e.session_id AND parent.run_id=e.run_id
                       WHERE receipt.node_id=n.node_id AND receipt.semantic_kind='tool' AND receipt.source_family='session_sdk'
                         AND receipt.carrier_digest=n.source_identity AND e.source_surface='copilot-sdk' COLLATE BINARY
                         AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
                         AND r.source_identity=e.event_id AND r.event_id=e.event_id
                         AND ((e.type='tool.execution_start' AND e.source_event_id IS NOT NULL
                               AND n.source_identity=local_workspace_semantic_digest('session_sdk_tool',run.native_run_id,e.source_event_id))
                           OR (e.type='tool.execution_complete' AND parent.type='tool.execution_start'
                               AND parent.source_adapter=e.source_adapter COLLATE BINARY
                               AND n.source_identity=local_workspace_semantic_digest('session_sdk_tool',run.native_run_id,parent.source_event_id)))
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
        if (node.SourceKind == "skill_invocation" && skillProjection?.State == "current")
        {
            var invocation = skillProjection.Invocations.SingleOrDefault(value =>
                string.Equals(value.CanonicalIdentity, node.SourceIdentity, StringComparison.Ordinal));
            if (invocation is not null)
            {
                var current = new List<LocalWorkspaceNodeSourceReferenceDetail>(2);
                if (invocation.OtelSourceIdentity is not null)
                    current.Add(persisted.SingleOrDefault(reference => reference.SourceKind == "skill_claim"
                        && reference.SourceIdentity == invocation.OtelSourceIdentity
                        && reference.TraceId == invocation.ProducerTraceId
                        && reference.SpanId == invocation.ProducerSpanId
                        && reference.EventId == invocation.OtelCarrierEventId)
                        ?? new("skill_claim", invocation.OtelSourceIdentity, invocation.ProducerTraceId,
                            invocation.ProducerSpanId, invocation.OtelCarrierEventId));
                if (invocation.SdkSourceIdentity is not null)
                    current.Add(persisted.SingleOrDefault(reference => reference.SourceKind == "skill_claim"
                        && reference.SourceIdentity == invocation.SdkSourceIdentity
                        && reference.TraceId is null && reference.SpanId is null
                        && reference.EventId == invocation.SdkCarrierEventId)
                        ?? new("skill_claim", invocation.SdkSourceIdentity, null, null, invocation.SdkCarrierEventId));
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
        if (projection?.State == "certification_pending")
            return nodes.Select(node => node.SourceKind == "skill_invocation" && node.SkillMetadata is not null
                ? node with { SkillMetadata = node.SkillMetadata with { CurrentValidState = "certification_pending" } }
                : node).ToArray();
        if (projection?.State != "current") return nodes;
        var current = projection.Invocations.ToDictionary(static invocation => invocation.CanonicalIdentity, StringComparer.Ordinal);
        return nodes.Select(node => node.SourceKind == "skill_invocation" && current.TryGetValue(node.SourceIdentity, out var invocation)
            ? node with
            {
                SkillMetadata = new("current",
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
            ? await ReadTimelineContext(c, t, request, token)
            : [];
        var predicate = request.Kind switch
        {
            LocalRepositorySessionDetailRequestKind.Summary => "session_id=$session_id AND source_kind='execution_root'",
            LocalRepositorySessionDetailRequestKind.Timeline when request.ExecutionId is null => "session_id=$session_id AND source_kind='execution_root'",
            LocalRepositorySessionDetailRequestKind.Timeline when request.ParentNodeId is null => "session_id=$session_id AND execution_id=$execution_id AND (parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE session_id=$session_id AND execution_id=$execution_id AND source_kind='execution_root') OR (kind='unknown_relation_group' AND parent_node_id IS NULL))",
            LocalRepositorySessionDetailRequestKind.Timeline => "session_id=$session_id AND execution_id=$execution_id AND parent_node_id=$parent_node_id",
            _ => """
                session_id=$session_id AND (node_id=$node_id
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
        var admittedSkillIds = skillProjection is not null && skillProjection.State is "current" or "certification_pending"
            ? skillProjection.Invocations.Select(static invocation => invocation.CanonicalIdentity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [];
        var skillPredicate = admittedSkillIds.Length == 0
            ? "source_kind<>'skill_invocation'"
            : "(source_kind<>'skill_invocation' OR source_identity IN (SELECT CAST(value AS TEXT) FROM json_each($skill_ids)))";
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline)
            predicate += " AND " + skillPredicate;
        var limit = request.Kind switch
        {
            LocalRepositorySessionDetailRequestKind.Summary => 257,
            LocalRepositorySessionDetailRequestKind.Node => 4097,
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
                || request.Kind == LocalRepositorySessionDetailRequestKind.Node && rows.Count == MaximumNodes)
                throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49),reader.GetInt64(50)));
        }
        return rows.ToArray();
    }

    private static async Task<LocalWorkspaceNodeDetail[]> ReadTimelineContext(
        SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request, CancellationToken token)
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
        using var command = Command(c, t, $"""
            SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              trace_id,span_id,event_id,COALESCE(children.child_count,0)
            FROM local_workspace_nodes
            LEFT JOIN (SELECT parent_node_id child_parent_id,COUNT(*) child_count FROM local_workspace_nodes WHERE session_id=$session_id AND parent_node_id IS NOT NULL GROUP BY parent_node_id) children
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
        var rows = new List<LocalWorkspaceNodeDetail>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49),reader.GetInt64(50)));
        if (request.ExecutionId is not null && rows.Count(static row => row.SourceKind == "execution_root") != 1
            && await ExecutionExists(c, t, request, token))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        return rows.DistinctBy(static row => row.NodeId, StringComparer.Ordinal).ToArray();
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
            ) WHERE ordinal<=201 ORDER BY node_id,relation_kind,source_ordinal,related_node_id;
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
        command.CommandText=$"SELECT c.node_id,c.part,{EffectiveContentAvailabilitySql},c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token FROM local_workspace_node_content_refs c LEFT JOIN retention_items i ON i.item_id=c.retention_item_id LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id WHERE c.node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY c.node_id,c.part;";
        command.Parameters.AddWithValue("$now", Canonical(acceptedAt));
        command.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(ids));var rows=new List<LocalWorkspaceContentAvailability>();using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))rows.Add(Content(reader));return rows.ToArray();
    }

    private async Task<LocalWorkspaceContentAvailability[]> ReadSummaryContent(SqliteConnection c, SqliteTransaction t, string sessionId, DateTimeOffset acceptedAt, CancellationToken token)
    {
        statementObserver?.Invoke("detail-summary-content");
        using var command = Command(c, t, $$"""
            SELECT c.node_id,c.part,{{EffectiveContentAvailabilitySql}},c.source_item_id,c.revision_input,c.store_kind,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token
            FROM local_workspace_node_content_refs c
            JOIN local_workspace_nodes n ON n.node_id=c.node_id
            JOIN local_workspace_sessions s ON s.session_id=n.session_id
            LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
            LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id
            WHERE n.session_id=$session_id AND c.part='instruction' AND c.source_item_id=s.label_source_identity
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
        var nativeIds = new List<string>();
        using (var command = Command(c,t,"SELECT DISTINCT native_session_id FROM session_native_ids WHERE session_id=$session_id ORDER BY native_session_id COLLATE BINARY;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) nativeIds.Add(reader.GetString(0));
        var versions = new List<string>();
        using (var command = Command(c,t,"SELECT value FROM (SELECT source_application_version value FROM session_events WHERE session_id=$session_id UNION SELECT adapter_version FROM session_events WHERE session_id=$session_id UNION SELECT normalization_version FROM session_events WHERE session_id=$session_id) WHERE value IS NOT NULL AND trim(value)<>'' ORDER BY value COLLATE BINARY;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) versions.Add(reader.GetString(0));
        string? source = null; long? additional = null;
        using (var command = Command(c,t,"SELECT label_source_identity,(SELECT COUNT(*)-1 FROM session_events WHERE session_id=$session_id AND type IN ('user.message','UserPromptSubmit')) FROM local_workspace_sessions WHERE session_id=$session_id;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) if(await reader.ReadAsync(token)){source=S(reader,0);additional=Math.Max(0,reader.GetInt64(1));}
        return new(Array.AsReadOnly(nativeIds.ToArray()),Array.AsReadOnly(versions.ToArray()),source,additional);
    }

    private static async Task<string> ReadCanonicalRevisionInput(
        SqliteConnection c, SqliteTransaction t, string sessionId, DateTimeOffset acceptedAt,
        SkillProjectionCurrentInvocationProjection? skillProjection, string registryIdentity, CancellationToken token)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-revision-input\0v2\0typed-sqlite-value\0"));
        var statements = new[]
        {
            "SELECT * FROM sessions WHERE session_id=$session_id",
            "SELECT * FROM session_native_ids WHERE session_id=$session_id ORDER BY source_surface,native_session_id",
            "SELECT * FROM session_runs WHERE session_id=$session_id ORDER BY run_id",
            "SELECT * FROM session_events WHERE session_id=$session_id ORDER BY event_id",
            "SELECT m.* FROM monitor_spans m WHERE EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=$session_id AND r.trace_id=m.trace_id) ORDER BY m.raw_record_id,m.span_ordinal",
            "SELECT r.id,r.source,r.trace_id,r.received_at,r.schema_version,r.retention_owner_token FROM raw_records r WHERE EXISTS(SELECT 1 FROM monitor_spans m JOIN session_runs s ON s.trace_id=m.trace_id WHERE s.session_id=$session_id AND m.raw_record_id=r.id) ORDER BY r.id",
            "SELECT f.* FROM local_workspace_span_facts f JOIN monitor_spans m ON m.raw_record_id=f.raw_record_id AND m.span_ordinal=f.span_ordinal WHERE EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=$session_id AND r.trace_id=m.trace_id) ORDER BY f.raw_record_id,f.span_ordinal",
            "SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,c.retention_owner_token FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE e.session_id=$session_id ORDER BY c.event_id",
            "SELECT session_id,sort_group,sort_epoch_ms,label_state,label_text,label_source_identity,label_expires_at,status,completeness,source_state,model_state,timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed FROM local_workspace_sessions WHERE session_id=$session_id",
            "SELECT * FROM local_workspace_execution_headers WHERE session_id=$session_id ORDER BY execution_id LIMIT 257",
            "SELECT * FROM local_workspace_nodes WHERE session_id=$session_id ORDER BY node_id LIMIT 4097",
            "SELECT e.* FROM local_workspace_node_edges e JOIN local_workspace_nodes n ON n.node_id=e.node_id WHERE n.session_id=$session_id ORDER BY e.node_id,e.related_node_id,e.relation_kind",
            "SELECT c.* FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id ORDER BY c.node_id,c.part",
            "SELECT r.* FROM local_workspace_semantic_receipts r JOIN local_workspace_nodes n ON n.node_id=r.node_id WHERE n.session_id=$session_id ORDER BY r.node_id",
            "SELECT r.* FROM local_workspace_node_source_references r JOIN local_workspace_nodes n ON n.node_id=r.node_id WHERE n.session_id=$session_id ORDER BY r.node_id,r.source_ordinal",
            "SELECT m.* FROM local_workspace_tool_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT m.* FROM local_workspace_skill_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT m.* FROM local_workspace_subagent_lifecycle m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.session_id=$session_id ORDER BY m.node_id",
            "SELECT x.* FROM local_workspace_content_tombstones x JOIN session_events e ON e.event_id=x.source_item_id WHERE e.session_id=$session_id ORDER BY x.store_kind,x.source_item_id,x.part",
            "SELECT i.* FROM skill_projection_invocations i WHERE i.session_id=$session_id ORDER BY i.generation_id,i.invocation_id",
            "SELECT c.* FROM skill_projection_sdk_claims c WHERE c.session_id=$session_id ORDER BY c.claim_id",
            "SELECT s.* FROM skill_invocation_snapshots s WHERE s.session_id=$session_id ORDER BY s.snapshot_id",
            "SELECT r.* FROM skill_invocation_snapshot_receipts r JOIN skill_invocation_snapshots s ON s.snapshot_id=r.snapshot_id WHERE s.session_id=$session_id ORDER BY r.snapshot_id",
            "SELECT h.* FROM skill_projection_trace_heads h WHERE EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=$session_id AND r.trace_id=h.trace_id) ORDER BY h.trace_id",
            "SELECT i.store_instance_id,i.store_kind,i.source_item_id,i.receipt_version,i.captured_at,i.expires_at,i.state,i.revision,t.receipt_at,t.deleted_at FROM retention_items i LEFT JOIN retention_tombstones t ON t.item_id=i.item_id WHERE EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id AND c.retention_item_id=i.item_id) ORDER BY i.item_id"
        };
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
            AppendRevisionValue(hash, invocation.CanonicalIdentity);
        using (var command = Command(c, t, $$"""
            SELECT c.node_id,c.part,{{EffectiveContentAvailabilitySql}}
            FROM local_workspace_node_content_refs c
            JOIN local_workspace_nodes n ON n.node_id=c.node_id
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

    private const string EffectiveContentAvailabilitySql = """
        CASE
          WHEN c.availability_state<>'available' THEN c.availability_state
          WHEN i.item_id IS NULL OR tmb.item_id IS NOT NULL OR i.deleted_at IS NOT NULL THEN 'deleted'
          WHEN i.read_denied_at IS NOT NULL OR i.error_code IS NOT NULL THEN 'read_denied'
          WHEN i.state IN ('expired_pending_deletion','deletion_queued','deleting','deletion_failed')
            OR (i.state='expiring' AND i.expires_at COLLATE BINARY <= $now COLLATE BINARY) THEN 'expired'
          WHEN i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY) THEN 'available'
          ELSE 'invalid'
        END
        """;

    private static string Canonical(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction t,string sql,string sessionId){var command=c.CreateCommand();command.Transaction=t;command.CommandText=sql;command.Parameters.AddWithValue("$session_id",sessionId);return command;}
    private static string? S(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static long? L(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static LocalWorkspaceActivityFacts Activity(SqliteDataReader r,int i)=>new(Fact(r,i),Fact(r,i+2),Fact(r,i+4),Fact(r,i+6),Fact(r,i+8));
    private static LocalWorkspaceFact<long> Fact(SqliteDataReader r,int i)=>new(r.GetString(i),L(r,i+1));
    private static LocalWorkspaceTokenFacts Tokens(SqliteDataReader r,int i)=>new(r.GetString(i),r.GetString(i+1),r.GetInt64(i+2),r.GetInt64(i+3),Fact(r,i+4),Fact(r,i+6),Fact(r,i+8),Fact(r,i+10),Fact(r,i+12),Fact(r,i+14),Fact(r,i+16),Fact(r,i+18));
    private sealed record Metadata(IReadOnlyList<string> NativeSessionIds,IReadOnlyList<string> Versions,string? InstructionSourceIdentity,long? InstructionAdditionalCount);
}

internal sealed class LocalWorkspaceSessionDetailException(string error) : Exception(error)
{
    internal string Error { get; } = error;
}

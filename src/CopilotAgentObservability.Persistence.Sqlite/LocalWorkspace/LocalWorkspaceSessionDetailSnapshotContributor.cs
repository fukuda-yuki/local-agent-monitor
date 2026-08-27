using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionDetailSnapshotContributor : ILocalWorkspaceSessionDetailSnapshotContributor
{
    internal const string BoundsSql = """
        SELECT
          EXISTS(SELECT 1 FROM local_workspace_execution_headers WHERE session_id=$session_id LIMIT 1 OFFSET 256),
          EXISTS(SELECT 1 FROM local_workspace_nodes WHERE session_id=$session_id LIMIT 1 OFFSET 4096);
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
        await ValidateBounds(connection, transaction, sessionId, token);
        if (request.Kind is LocalRepositorySessionDetailRequestKind.Node or LocalRepositorySessionDetailRequestKind.Content)
            await ValidateNodeAncestry(connection, transaction, request, token);
        var projectedNodes = await ReadNodes(connection, transaction, request, token);
        var admittedSkillIdentities = skillProjection?.State == "current"
            ? skillProjection.Invocations.Select(static invocation => invocation.CanonicalIdentity).ToHashSet(StringComparer.Ordinal)
            : [];
        var excludedSkillNodes = projectedNodes.Where(node => node.SourceKind == "skill_invocation"
            && !admittedSkillIdentities.Contains(node.SourceIdentity)).ToArray();
        var nodes = projectedNodes.Except(excludedSkillNodes).ToArray();
        var executionIds = nodes.Select(static node => node.ExecutionId).Distinct(StringComparer.Ordinal).ToArray();
        var executions = ApplyCurrentSkillActivity(
            await ReadExecutions(connection, transaction, request, executionIds, token), skillProjection);
        nodes = ApplyCurrentSkillActivity(nodes, executions, excludedSkillNodes);
        var nodeIds = nodes.Select(static node => node.NodeId).ToArray();
        var edges = request.Kind == LocalRepositorySessionDetailRequestKind.Node
            ? await ReadEdges(connection, transaction, [request.NodeId!], token)
            : [];
        var metadata = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? await ReadMetadata(connection, transaction, sessionId, token)
            : new Metadata([], [], null, null);
        var content = request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            ? await ReadSummaryContent(connection, transaction, sessionId, acceptedAt, token)
            : await ReadContent(connection, transaction, nodeIds, acceptedAt, token);
        var revision = await ReadCanonicalRevisionInput(connection, transaction, sessionId, acceptedAt, skillProjection, token);
        var registryIdentity = ReadRegistryIdentity();
        return new(Array.AsReadOnly(executions), Array.AsReadOnly(nodes), Array.AsReadOnly(edges), Array.AsReadOnly(content),
            metadata.NativeSessionIds, metadata.Versions, metadata.InstructionSourceIdentity, metadata.InstructionAdditionalCount, revision, registryIdentity);
    }

    private static LocalWorkspaceExecutionDetail[] ApplyCurrentSkillActivity(
        LocalWorkspaceExecutionDetail[] executions,
        SkillProjectionCurrentInvocationProjection? projection)
    {
        var current = projection?.State == "current";
        return executions.Select(execution =>
        {
            var count = current
                ? projection!.Invocations.LongCount(invocation =>
                    string.Equals(invocation.ExecutionSourceKind, execution.SourceKind, StringComparison.Ordinal)
                    && string.Equals(invocation.ExecutionSourceIdentity, execution.SourceIdentity, StringComparison.Ordinal))
                : 0;
            var skill = current && count > 0
                ? new LocalWorkspaceFact<long>("recorded", count)
                : projection is null
                    ? new LocalWorkspaceFact<long>("not_observed", null)
                    : new LocalWorkspaceFact<long>(projection.State == "current" ? "not_observed" : projection.State, null);
            return execution with { Activity = execution.Activity with { Skill = skill } };
        }).ToArray();
    }

    private static LocalWorkspaceNodeDetail[] ApplyCurrentSkillActivity(
        LocalWorkspaceNodeDetail[] nodes,
        LocalWorkspaceExecutionDetail[] executions,
        LocalWorkspaceNodeDetail[] excluded)
    {
        var excludedByParent = excluded.Where(static node => node.ParentNodeId is not null)
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
        CancellationToken token)
    {
        using var command = Command(connection, transaction, BoundsSql, sessionId);
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)
            || reader.GetInt64(0) != 0
            || reader.GetInt64(1) != 0)
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
            SELECT execution_id,session_id,source_kind,source_identity,source_ordinal,lifecycle,status,model,trace_id,
              time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              r.source_surface,
              CASE WHEN (SELECT COUNT(DISTINCT e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND e.source_application_version IS NOT NULL)=1
                THEN (SELECT MIN(e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity) END,
              COALESCE(children.child_count,0)
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
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),reader.GetString(5),reader.GetString(6),S(reader,7),S(reader,8),reader.GetString(9),L(reader,10),L(reader,11),L(reader,12),Activity(reader,13),Tokens(reader,23),S(reader,43),S(reader,44),reader.GetInt64(45)));
        }
        return rows.ToArray();
    }

    private async Task<LocalWorkspaceNodeDetail[]> ReadNodes(SqliteConnection c, SqliteTransaction t, LocalRepositorySessionDetailRequest request, CancellationToken token)
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
        var limit = request.Kind switch
        {
            LocalRepositorySessionDetailRequestKind.Summary => 257,
            LocalRepositorySessionDetailRequestKind.Node => 4097,
            _ => request.Limit + 1
        };
        using var command = Command(c, t, $"""
            SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              trace_id,span_id,event_id,COALESCE(children.child_count,0)
            FROM local_workspace_nodes
            LEFT JOIN (SELECT parent_node_id child_parent_id,COUNT(*) child_count FROM local_workspace_nodes WHERE session_id=$session_id AND parent_node_id IS NOT NULL GROUP BY parent_node_id) children
              ON children.child_parent_id=local_workspace_nodes.node_id
            WHERE {predicate} ORDER BY execution_id,
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
        SkillProjectionCurrentInvocationProjection? skillProjection, CancellationToken token)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-revision-input\0v1\0"));
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
            "SELECT * FROM local_workspace_sessions WHERE session_id=$session_id",
            "SELECT * FROM local_workspace_execution_headers WHERE session_id=$session_id ORDER BY execution_id LIMIT 257",
            "SELECT * FROM local_workspace_nodes WHERE session_id=$session_id ORDER BY node_id LIMIT 4097",
            "SELECT e.* FROM local_workspace_node_edges e JOIN local_workspace_nodes n ON n.node_id=e.node_id WHERE n.session_id=$session_id ORDER BY e.node_id,e.related_node_id,e.relation_kind",
            "SELECT c.* FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id ORDER BY c.node_id,c.part",
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
            while(await reader.ReadAsync(token)) for(var i=0;i<reader.FieldCount;i++) Append(reader.GetValue(i));
            hash.AppendData([0xff]);
        }
        Append(skillProjection?.State ?? "not_observed");
        foreach (var invocation in skillProjection?.Invocations.OrderBy(static value => value.CanonicalIdentity, StringComparer.Ordinal)
            ?? Enumerable.Empty<SkillProjectionCanonicalInvocation>())
            Append(invocation.CanonicalIdentity);
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
                Append(reader.GetString(0));
                Append(reader.GetString(1));
                Append(reader.GetString(2));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append(object value){var text=value is DBNull?"<null>":value is byte[] bytes?Convert.ToHexString(bytes):Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)!;var data=System.Text.Encoding.UTF8.GetBytes(text);Span<byte> length=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length,data.Length);hash.AppendData(length);hash.AppendData(data);}
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

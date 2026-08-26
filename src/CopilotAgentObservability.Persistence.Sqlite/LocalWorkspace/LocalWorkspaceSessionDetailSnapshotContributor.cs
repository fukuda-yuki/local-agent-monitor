using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionDetailSnapshotContributor : ILocalWorkspaceSessionDetailSnapshotContributor
{
    private const int MaximumExecutions = 256;
    private const int MaximumNodes = 4096;
    private readonly Action<string>? statementObserver;
    private readonly ISkillRegistryGenerationAuthority? registryAuthority;

    internal LocalWorkspaceSessionDetailSnapshotContributor(Action<string>? statementObserver = null, ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        this.statementObserver = statementObserver;
        this.registryAuthority = registryAuthority;
    }

    public ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken) =>
        transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadAsync(connection, sqliteTransaction, sessionId, token), cancellationToken);

    private async ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId, CancellationToken token)
    {
        var executions = await ReadExecutions(connection, transaction, sessionId, token);
        var nodes = await ReadNodes(connection, transaction, sessionId, token);
        var nodeIds = nodes.Select(static node => node.NodeId).ToArray();
        var edges = await ReadEdges(connection, transaction, nodeIds, token);
        var content = await ReadContent(connection, transaction, nodeIds, token);
        var metadata = await ReadMetadata(connection, transaction, sessionId, token);
        var revision = await ReadCanonicalRevisionInput(connection, transaction, sessionId, token);
        var registryIdentity = ReadRegistryIdentity();
        return new(Array.AsReadOnly(executions), Array.AsReadOnly(nodes), Array.AsReadOnly(edges), Array.AsReadOnly(content),
            metadata.NativeSessionIds, metadata.Versions, metadata.InstructionSourceIdentity, metadata.InstructionAdditionalCount, revision, registryIdentity);
    }

    private string ReadRegistryIdentity()
    {
        if (registryAuthority is null) return "registry-unavailable";
        var capture = registryAuthority.CaptureGeneration() ?? throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (!registryAuthority.TryAcquireGenerationReadLease(capture, out var lease)) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        using (lease)
        {
            if (!registryAuthority.VerifyGenerationIdentity(capture, lease)) throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            return registryAuthority.GetCanonicalGenerationIdentity(capture, lease);
        }
    }

    private async Task<LocalWorkspaceExecutionDetail[]> ReadExecutions(SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        statementObserver?.Invoke("detail-executions");
        using var command = Command(c, t, """
            SELECT execution_id,session_id,source_kind,source_identity,source_ordinal,lifecycle,status,model,trace_id,
              time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              r.source_surface,
              CASE WHEN (SELECT COUNT(DISTINCT e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND e.source_application_version IS NOT NULL)=1
                THEN (SELECT MIN(e.source_application_version) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity) END
            FROM local_workspace_execution_headers h
            LEFT JOIN (SELECT session_id run_session_id,run_id,source_surface FROM session_runs) r ON r.run_session_id=h.session_id AND r.run_id=h.source_identity
            WHERE h.session_id=$session_id
            ORDER BY CASE time_authority WHEN 'recorded' THEN 0 WHEN 'missing' THEN 1 ELSE 2 END,
              CASE WHEN time_authority='recorded' THEN start_utc_ticks END DESC,source_ordinal,execution_id LIMIT 257;
            """, sessionId);
        var rows = new List<LocalWorkspaceExecutionDetail>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (rows.Count == MaximumExecutions) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),reader.GetString(5),reader.GetString(6),S(reader,7),S(reader,8),reader.GetString(9),L(reader,10),L(reader,11),L(reader,12),Activity(reader,13),Tokens(reader,23),S(reader,43),S(reader,44)));
        }
        return rows.ToArray();
    }

    private async Task<LocalWorkspaceNodeDetail[]> ReadNodes(SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        statementObserver?.Invoke("detail-nodes");
        using var command = Command(c, t, """
            SELECT node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,
              skill_activity_state,skill_activity_count,tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count,error_activity_state,error_activity_count,retry_activity_state,retry_activity_count,
              token_authority,token_state,available_execution_count,total_execution_count,input_token_state,input_tokens,output_token_state,output_tokens,total_token_state,total_tokens,reasoning_token_state,reasoning_tokens,cache_read_token_state,cache_read_tokens,cache_creation_token_state,cache_creation_tokens,new_input_token_state,new_input_tokens,cache_read_ratio_state,cache_read_ratio_basis_points,
              trace_id,span_id,event_id
            FROM local_workspace_nodes WHERE session_id=$session_id ORDER BY execution_id,source_ordinal,node_id LIMIT 4097;
            """, sessionId);
        var rows = new List<LocalWorkspaceNodeDetail>();
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (rows.Count == MaximumNodes) throw new LocalWorkspaceSessionDetailException("workspace_too_large");
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),S(reader,6),reader.GetString(7),reader.GetString(8),reader.GetString(9),S(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),L(reader,14),L(reader,15),L(reader,16),Activity(reader,17),Tokens(reader,27),S(reader,47),S(reader,48),S(reader,49)));
        }
        return rows.ToArray();
    }

    private async Task<LocalWorkspaceNodeEdgeDetail[]> ReadEdges(SqliteConnection c, SqliteTransaction t, string[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        statementObserver?.Invoke("detail-edges");
        using var command = c.CreateCommand(); command.Transaction=t;
        command.CommandText="SELECT node_id,related_node_id,relation_kind,relationship_authority,source_ordinal FROM local_workspace_node_edges WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY node_id,relation_kind,source_ordinal,related_node_id;";
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        var rows=new List<LocalWorkspaceNodeEdgeDetail>(); using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token)) rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4)));
        return rows.ToArray();
    }

    private async Task<LocalWorkspaceContentAvailability[]> ReadContent(SqliteConnection c, SqliteTransaction t, string[] ids, CancellationToken token)
    {
        if(ids.Length==0)return [];
        statementObserver?.Invoke("detail-content"); using var command=c.CreateCommand();command.Transaction=t;
        command.CommandText="SELECT node_id,part,availability_state,source_item_id,revision_input FROM local_workspace_node_content_refs WHERE node_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY node_id,part;";
        command.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(ids));var rows=new List<LocalWorkspaceContentAvailability>();using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));return rows.ToArray();
    }

    private static async Task<Metadata> ReadMetadata(SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        var nativeIds = new List<string>();
        using (var command = Command(c,t,"SELECT native_session_id FROM session_native_ids WHERE session_id=$session_id ORDER BY native_session_id COLLATE BINARY;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) nativeIds.Add(reader.GetString(0));
        var versions = new List<string>();
        using (var command = Command(c,t,"SELECT value FROM (SELECT source_application_version value FROM session_events WHERE session_id=$session_id UNION SELECT adapter_version FROM session_events WHERE session_id=$session_id UNION SELECT normalization_version FROM session_events WHERE session_id=$session_id) WHERE value IS NOT NULL AND trim(value)<>'' ORDER BY value COLLATE BINARY;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) while(await reader.ReadAsync(token)) versions.Add(reader.GetString(0));
        string? source = null; long? additional = null;
        using (var command = Command(c,t,"SELECT label_source_identity,(SELECT COUNT(*)-1 FROM session_events WHERE session_id=$session_id AND type IN ('user.message','UserPromptSubmit')) FROM local_workspace_sessions WHERE session_id=$session_id;",sessionId))
        using (var reader = await command.ExecuteReaderAsync(token)) if(await reader.ReadAsync(token)){source=S(reader,0);additional=Math.Max(0,reader.GetInt64(1));}
        return new(Array.AsReadOnly(nativeIds.ToArray()),Array.AsReadOnly(versions.ToArray()),source,additional);
    }

    private static async Task<string> ReadCanonicalRevisionInput(SqliteConnection c, SqliteTransaction t, string sessionId, CancellationToken token)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-revision-input\0v1\0"));
        var statements = new[]
        {
            "SELECT * FROM sessions WHERE session_id=$session_id",
            "SELECT * FROM session_native_ids WHERE session_id=$session_id ORDER BY source_surface,native_session_id",
            "SELECT * FROM session_runs WHERE session_id=$session_id ORDER BY run_id",
            "SELECT * FROM session_events WHERE session_id=$session_id ORDER BY event_id",
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
            "SELECT i.*,t.deleted_at FROM retention_items i LEFT JOIN retention_tombstones t ON t.item_id=i.item_id WHERE EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session_id AND c.retention_item_id=i.item_id) ORDER BY i.item_id"
        };
        foreach(var sql in statements)
        {
            using var command=Command(c,t,sql,sessionId);using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token)) for(var i=0;i<reader.FieldCount;i++) Append(reader.GetValue(i));
            hash.AppendData([0xff]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append(object value){var text=value is DBNull?"<null>":value is byte[] bytes?Convert.ToHexString(bytes):Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)!;var data=System.Text.Encoding.UTF8.GetBytes(text);Span<byte> length=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length,data.Length);hash.AppendData(length);hash.AppendData(data);}
    }

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

using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1SessionDetailApplication
{
    private const int MaximumResponseBytes = 8_388_608;
    private static readonly string[] Parts = ["instruction","tool_input","tool_result","error_message","subagent_input","event_content"];

    internal static byte[] SerializeSummary(LocalRepositorySessionDetailSnapshot snapshot) => Write(w =>
    {
        var scope=snapshot.Session; var session=(LocalWorkspaceProjectionRow)scope.Session; var detail=snapshot.Detail;
        w.WriteStartObject();w.WriteString("schema_version","local-monitor-session-summary.response.v1");w.WriteString("workspace_revision",snapshot.WorkspaceRevision);
        w.WritePropertyName("session");w.WriteStartObject();w.WriteString("session_id",session.SessionId);w.WriteString("status",session.Status);w.WriteString("completeness",session.Completeness);
        Assignment(w,scope);Archive(w,scope);w.WritePropertyName("instruction");w.WriteStartObject();w.WriteString("state",InstructionState(session.LabelState));if(session.LabelText is null)w.WriteNull("label");else w.WriteString("label",session.LabelText);Number(w,"additional_count",detail.InstructionAdditionalCount);w.WriteBoolean("content_available",detail.InstructionSourceIdentity is not null&&detail.Content.Any(c=>c.Part=="instruction"&&c.SourceItemId==detail.InstructionSourceIdentity&&c.State=="available"));w.WriteEndObject();
        Set(w,"source",session.Sources);Set(w,"model",session.Models);w.WritePropertyName("version");w.WriteStartObject();var versions=detail.Versions??[];w.WriteString("state",versions.Count==0?"not_observed":"recorded");w.WritePropertyName("values");JsonSerializer.Serialize(w,versions);w.WriteEndObject();
        w.WritePropertyName("timing");w.WriteStartObject();w.WriteString("state",session.TimingState);Nullable(w,"started_at",session.StartedAt);Nullable(w,"ended_at",session.EndedAt);Nullable(w,"last_seen_at",session.LastSeenAt);Number(w,"duration_ms",session.DurationMilliseconds);w.WriteEndObject();Tokens(w,session.Tokens);Activity(w,session.Activity);w.WritePropertyName("capture");w.WriteStartObject();w.WriteString("state",CaptureState(session.Completeness));w.WritePropertyName("notes");JsonSerializer.Serialize(w,session.CaptureNotes);w.WriteEndObject();w.WriteEndObject();
        w.WritePropertyName("executions");w.WriteStartArray();foreach(var execution in detail.Executions)Execution(w,execution,detail);w.WriteEndArray();
        w.WritePropertyName("technical_references");w.WriteStartObject();w.WritePropertyName("native_session_ids");JsonSerializer.Serialize(w,detail.NativeSessionIds??[]);w.WritePropertyName("trace_ids");JsonSerializer.Serialize(w,detail.Executions.Select(e=>e.TraceId).Where(static id=>id is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));w.WriteEndObject();w.WriteEndObject();
    });

    internal static byte[] SerializeTimeline(LocalRepositorySessionDetailSnapshot snapshot,string? executionId,string? parentNodeId,int limit,string? after,byte[] key)
    {
        var nodes=snapshot.Detail.Nodes;
        if(executionId is not null&&!snapshot.Detail.Executions.Any(e=>e.ExecutionId==executionId))throw new LocalMonitorV1SessionDetailException("execution_not_found");
        if(parentNodeId is not null&&!nodes.Any(n=>n.NodeId==parentNodeId&&n.ExecutionId==executionId))throw new LocalMonitorV1SessionDetailException("node_not_found");
        IEnumerable<LocalWorkspaceNodeDetail> query;
        if(executionId is null) query=nodes.Where(n=>n.SourceKind=="execution_root");
        else
        {
            var root=nodes.SingleOrDefault(n=>n.ExecutionId==executionId&&n.SourceKind=="execution_root");
            query=parentNodeId is null
                ? nodes.Where(n=>n.ExecutionId==executionId&&n.SourceKind!="execution_root"&&(n.ParentNodeId==root?.NodeId||n.Kind=="unknown_relation_group"&&n.ParentNodeId is null))
                : nodes.Where(n=>n.ExecutionId==executionId&&n.ParentNodeId==parentNodeId&&n.SourceKind!="execution_root");
        }
        query=query.OrderBy(TimeGroup).ThenBy(n=>n.TimeAuthority=="recorded"?n.StartUtcTicks:0).ThenBy(n=>n.SourceOrdinal).ThenBy(n=>n.NodeId,StringComparer.Ordinal);
        var filter=new LocalMonitorV1TimelineFilter(snapshot.Session.SessionId,snapshot.WorkspaceRevision,executionId,parentNodeId,limit);
        if(after is not null){if(!LocalMonitorV1TimelineCursor.TryDecode(after,key,filter,out var position)||!query.Any(n=>Compare(n,position)==0))throw new LocalMonitorV1SessionDetailException("invalid_cursor");query=query.Where(n=>Compare(n,position)>0);}
        var page=query.Take(limit+1).ToArray();var emitted=page.Take(limit).ToArray();
        return Write(w=>{w.WriteStartObject();w.WriteString("schema_version","local-monitor-session-timeline.response.v1");w.WriteString("workspace_revision",snapshot.WorkspaceRevision);w.WriteString("session_id",snapshot.Session.SessionId);Nullable(w,"execution_id",executionId);Nullable(w,"parent_node_id",parentNodeId);w.WritePropertyName("items");w.WriteStartArray();foreach(var node in emitted)NodeItem(w,node,snapshot.Detail);w.WriteEndArray();if(page.Length>emitted.Length){var n=emitted[^1];w.WriteString("next_cursor",LocalMonitorV1TimelineCursor.Encode(key,filter,new((byte)TimeGroup(n),n.TimeAuthority=="recorded"?n.StartUtcTicks!.Value:0,(ulong)n.SourceOrdinal,n.NodeId)));}else w.WriteNull("next_cursor");w.WriteEndObject();});
    }

    internal static byte[] SerializeNode(LocalRepositorySessionDetailSnapshot snapshot,string nodeId)
    {
        var node=snapshot.Detail.Nodes.SingleOrDefault(n=>n.NodeId==nodeId)??throw new LocalMonitorV1SessionDetailException("node_not_found");
        var execution=snapshot.Detail.Executions.Single(e=>e.ExecutionId==node.ExecutionId);
        var byId=snapshot.Detail.Nodes.ToDictionary(n=>n.NodeId,StringComparer.Ordinal);var path=new List<LocalWorkspaceNodeDetail>();var seen=new HashSet<string>(StringComparer.Ordinal);var parent=node.ParentNodeId;
        while(parent is not null){if(!seen.Add(parent)||!byId.TryGetValue(parent,out var value)||value.ExecutionId!=node.ExecutionId)throw new LocalMonitorV1SessionDetailException("node_not_found");path.Add(value);parent=value.ParentNodeId;}path.Reverse();
        var related=snapshot.Detail.Edges.Where(e=>e.NodeId==nodeId).ToArray();
        if(related.GroupBy(e=>e.RelationKind).Any(g=>g.Count()>200))throw new LocalMonitorV1SessionDetailException("workspace_too_large");
        if(snapshot.Detail.Nodes.Count(n=>n.ParentNodeId==nodeId)>200)throw new LocalMonitorV1SessionDetailException("workspace_too_large");
        return Write(w=>{w.WriteStartObject();w.WriteString("schema_version","local-monitor-session-node.response.v1");w.WriteString("workspace_revision",snapshot.WorkspaceRevision);w.WriteString("session_id",snapshot.Session.SessionId);w.WritePropertyName("execution");Execution(w,execution,snapshot.Detail);w.WritePropertyName("node");NodeItem(w,node,snapshot.Detail,true);w.WritePropertyName("parent_path");w.WriteStartArray();foreach(var p in path)NodeItem(w,p,snapshot.Detail);w.WriteEndArray();w.WritePropertyName("related");w.WriteStartObject();Related(w,"retry",related,node,snapshot.Detail);Related(w,"recovery",related,node,snapshot.Detail);w.WritePropertyName("children");w.WriteStartArray();foreach(var child in snapshot.Detail.Nodes.Where(n=>n.ParentNodeId==nodeId).OrderBy(TimeGroup).ThenBy(n=>n.TimeAuthority=="recorded"?n.StartUtcTicks:0).ThenBy(n=>n.SourceOrdinal).ThenBy(n=>n.NodeId,StringComparer.Ordinal).Take(201)){NodeItem(w,child,snapshot.Detail);}w.WriteEndArray();w.WriteEndObject();w.WritePropertyName("content");w.WriteStartObject();foreach(var part in Parts){var fact=snapshot.Detail.Content.SingleOrDefault(c=>c.NodeId==nodeId&&c.Part==part);var state=fact?.State??"not_captured";w.WritePropertyName(part);w.WriteStartObject();w.WriteString("state",state);w.WriteBoolean("available",state=="available");w.WriteEndObject();}w.WriteEndObject();w.WriteEndObject();});
    }

    private static void Execution(Utf8JsonWriter w,LocalWorkspaceExecutionDetail e,LocalWorkspaceSessionDetailContribution d){var root=d.Nodes.Single(n=>n.ExecutionId==e.ExecutionId&&n.SourceKind=="execution_root");w.WriteStartObject();w.WriteString("execution_id",e.ExecutionId);w.WriteString("node_id",root.NodeId);Nullable(w,"source",e.SourceSurface);Nullable(w,"model",e.Model);w.WriteString("lifecycle",e.Lifecycle);w.WriteString("status",e.Status);Timing(w,e.TimeAuthority,e.StartUtcTicks,e.EndUtcTicks,e.DurationMilliseconds);Tokens(w,e.Tokens);Activity(w,e.Activity);w.WriteNumber("child_count",e.ChildCount);w.WriteEndObject();}
    private static void NodeItem(Utf8JsonWriter w,LocalWorkspaceNodeDetail n,LocalWorkspaceSessionDetailContribution d,bool technical=false){w.WriteStartObject();w.WriteString("node_id",n.NodeId);w.WriteString("execution_id",n.ExecutionId);Nullable(w,"parent_node_id",n.ParentNodeId);w.WriteString("relationship_authority",n.RelationshipAuthority);w.WriteString("kind",n.Kind);w.WritePropertyName("name");w.WriteStartObject();w.WriteString("state",n.NameState);Nullable(w,"text",n.NameText);w.WriteEndObject();w.WriteString("lifecycle",n.Lifecycle);w.WriteString("status",n.Status);Timing(w,n.TimeAuthority,n.StartUtcTicks,n.EndUtcTicks,n.DurationMilliseconds);Activity(w,n.Activity);Tokens(w,n.Tokens);w.WriteNumber("child_count",n.ChildCount);w.WritePropertyName("content_parts");JsonSerializer.Serialize(w,Parts.Where(part=>d.Content.Any(c=>c.NodeId==n.NodeId&&c.Part==part)));if(technical){w.WritePropertyName("technical_references");w.WriteStartObject();w.WriteString("source_kind",n.SourceKind);w.WriteString("source_identity",n.SourceIdentity);Nullable(w,"trace_id",n.TraceId);Nullable(w,"span_id",n.SpanId);Nullable(w,"event_id",n.EventId);w.WriteEndObject();}w.WriteEndObject();}
    private static void Related(Utf8JsonWriter w,string kind,LocalWorkspaceNodeEdgeDetail[] edges,LocalWorkspaceNodeDetail node,LocalWorkspaceSessionDetailContribution d){w.WritePropertyName(kind);w.WriteStartArray();foreach(var related in edges.Where(e=>e.RelationKind==kind).Select(e=>d.Nodes.SingleOrDefault(n=>n.NodeId==e.RelatedNodeId&&n.ExecutionId==node.ExecutionId)).Where(n=>n is not null).Cast<LocalWorkspaceNodeDetail>().OrderBy(TimeGroup).ThenBy(n=>n.TimeAuthority=="recorded"?n.StartUtcTicks:0).ThenBy(n=>n.SourceOrdinal).ThenBy(n=>n.NodeId,StringComparer.Ordinal)){NodeItem(w,related,d);}w.WriteEndArray();}
    private static int Compare(LocalWorkspaceNodeDetail n,LocalMonitorV1TimelinePosition p){var c=TimeGroup(n).CompareTo(p.TimeGroup);if(c!=0)return c;c=(n.TimeAuthority=="recorded"?n.StartUtcTicks!.Value:0).CompareTo(p.UtcTicks);if(c!=0)return c;c=((ulong)n.SourceOrdinal).CompareTo(p.SourceOrdinal);return c!=0?c:StringComparer.Ordinal.Compare(n.NodeId,p.NodeId);}
    private static int TimeGroup(LocalWorkspaceNodeDetail n)=>n.TimeAuthority switch{"recorded"=>0,"missing"=>1,_=>2};
    private static void Assignment(Utf8JsonWriter w,LocalRepositoryScopeSessionSnapshot s){w.WritePropertyName("assignment");w.WriteStartObject();w.WriteString("state",Snake(s.AssignmentState));w.WriteString("authority",Snake(s.AssignmentAuthority));w.WriteNumber("revision",s.AssignmentRevision);Nullable(w,"repository_id",s.RepositoryId);w.WritePropertyName("candidate_repository_ids");JsonSerializer.Serialize(w,s.CandidateRepositoryIds);w.WriteEndObject();}
    private static void Archive(Utf8JsonWriter w,LocalRepositoryScopeSessionSnapshot s){w.WritePropertyName("archive");w.WriteStartObject();w.WriteString("state",s.ArchiveState==LocalArchiveState.Active?"active":"archived");w.WriteNumber("revision",s.ArchiveRevision);w.WriteBoolean("effectively_eligible",s.IsEffectivelyEligible);Nullable(w,"exclusion_reason",s.ArchiveExclusionReason);w.WriteEndObject();}
    private static void Set(Utf8JsonWriter w,string name,LocalWorkspaceSetFact f){w.WritePropertyName(name);w.WriteStartObject();w.WriteString("state",f.State);w.WritePropertyName("values");JsonSerializer.Serialize(w,f.Values);w.WriteEndObject();}
    private static void Activity(Utf8JsonWriter w,LocalWorkspaceActivityFacts a){w.WritePropertyName("activity");w.WriteStartObject();Count(w,"skill",a.Skill);Count(w,"tool",a.Tool);Count(w,"subagent",a.Subagent);Count(w,"error",a.Error);Count(w,"retry",a.Retry);w.WriteEndObject();}
    private static void Count(Utf8JsonWriter w,string n,LocalWorkspaceFact<long> f){w.WritePropertyName(n);w.WriteStartObject();w.WriteString("state",f.State);Number(w,"count",f.Value);w.WriteEndObject();}
    private static void Tokens(Utf8JsonWriter w,LocalWorkspaceTokenFacts t){w.WritePropertyName("tokens");w.WriteStartObject();w.WriteString("authority",t.Authority);w.WriteString("state",t.State);w.WriteNumber("available_execution_count",t.AvailableExecutionCount);w.WriteNumber("total_execution_count",t.TotalExecutionCount);Value(w,"input",t.Input);Value(w,"output",t.Output);Value(w,"total",t.Total);Value(w,"reasoning",t.Reasoning);Value(w,"cache_read",t.CacheRead);Value(w,"cache_creation",t.CacheCreation);Value(w,"new_input",t.NewInput);Value(w,"cache_read_ratio_basis_points",t.CacheReadRatioBasisPoints);w.WriteEndObject();}
    private static void Value(Utf8JsonWriter w,string n,LocalWorkspaceFact<long> f){w.WritePropertyName(n);w.WriteStartObject();w.WriteString("state",f.State);Number(w,"value",f.Value);w.WriteEndObject();}
    private static void Timing(Utf8JsonWriter w,string state,long? start,long? end,long? duration){w.WritePropertyName("timing");w.WriteStartObject();w.WriteString("state",state);Nullable(w,"started_at",Instant(start));Nullable(w,"ended_at",Instant(end));Number(w,"duration_ms",duration);w.WriteEndObject();}
    private static string? Instant(long? ticks)=>ticks is null?null:new DateTimeOffset(ticks.Value,TimeSpan.Zero).ToString("O",CultureInfo.InvariantCulture);
    private static void Nullable(Utf8JsonWriter w,string n,string? v){if(v is null)w.WriteNull(n);else w.WriteString(n,v);}private static void Number(Utf8JsonWriter w,string n,long? v){if(v is null)w.WriteNull(n);else w.WriteNumber(n,v.Value);}
    private static string Snake<T>(T value)where T:Enum=>value.ToString().Replace("ExplicitlyUnassigned","explicitly_unassigned",StringComparison.Ordinal).ToLowerInvariant();
    private static string InstructionState(string state)=>state switch{"recorded"=>"recorded","not_captured"=>"not_captured","expired"=>"expired","invalid" or "projection_invalid"=>"invalid",_=>"not_observed"};private static string CaptureState(string value)=>value switch{"full"=>"complete","rich" or "partial"=>"partial","unbound"=>"not_observed",_=>"invalid"};
    private static byte[] Write(Action<Utf8JsonWriter> action){using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream,new(){Indented=false,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))action(writer);var bytes=stream.ToArray();if(bytes.Length>MaximumResponseBytes)throw new LocalMonitorV1SessionDetailException("workspace_too_large");return bytes;}
}

internal sealed class LocalMonitorV1SessionDetailException(string error):Exception(error){internal string Error{get;}=error;}
